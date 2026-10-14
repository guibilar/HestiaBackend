using System.Text.Json;
using Hestia.Platform.Events;
using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hestia.Api.Events;

/// <summary>The settings of the module outbox sender (<c>Events:Outbox</c>, T38).</summary>
public sealed class OutboxOptions
{
    /// <summary>
    /// False stops the sender: durable module events stay in their outbox until a host starts with it on. For tests and
    /// diagnostics. The host reads it when it starts.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the sender reads the outboxes without a signal (a commit wakes it at once).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The wait after the first failed delivery. It doubles after each failure, up to 10 minutes (or up to this value, if
    /// it is more than 10 minutes).
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>After this many failed deliveries, the sender stops and keeps the row (with its last error).</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>
    /// The readiness check <c>events-outbox</c> is Degraded when a row is older than this (T37): the sender is off, or
    /// it fails again and again.
    /// </summary>
    public TimeSpan WarnAfter { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// The outbox format of module events: the full type name and System.Text.Json. Only the event types of the modules'
/// <c>.Contracts</c> projects (<see cref="HestiaEvents.ModuleEventTypes"/>) can go to an outbox.
/// </summary>
internal sealed class ModuleEventSerializer(IEnumerable<Type> eventTypes) : IModuleEventSerializer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    private readonly Dictionary<string, Type> _types =
        eventTypes.ToDictionary(type => type.FullName ?? type.Name, StringComparer.Ordinal);

    public string TypeName(object @event)
    {
        var type = @event.GetType();
        if (type.FullName is { } name && _types.TryGetValue(name, out var known) && known == type)
        {
            return name;
        }
        throw new InvalidOperationException(
            $"The event {type.Name} cannot go to an event outbox. A durable module event must be a class in a module's " +
            ".Contracts project that implements IIntegrationEvent.");
    }

    public string Serialize(object @event) => JsonSerializer.Serialize(@event, @event.GetType(), Json);

    public object Deserialize(string typeName, string payload)
    {
        if (!_types.TryGetValue(typeName, out var type))
        {
            throw new InvalidOperationException(
                $"The outbox has an event of the type {typeName}, and no module has this event type now.");
        }
        return JsonSerializer.Deserialize(payload, type, Json)
            ?? throw new InvalidOperationException($"The outbox has an empty event of the type {typeName}.");
    }
}

/// <summary>
/// The module outbox sender (T38). For each module with durable events, it reads the rows of the module's outbox in
/// order, delivers each event to all its consumers (<see cref="IEventPublisher.DeliverAsync"/>), and deletes the row
/// when the consumers finished. It starts when the host has started, after a commit signal
/// (<see cref="ModuleOutboxSignal"/>), and at each <see cref="OutboxOptions.PollInterval"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>At least once: a stop after a delivery and before the delete delivers that event again at the next start.
/// The consumers are idempotent (the module inbox, T36), so the data changes once.</item>
/// <item>A failed delivery stays in the outbox and gets a new try later (<see cref="OutboxOptions.RetryDelay"/>, doubled
/// each time). The next events do not wait for it, so the order is kept only while deliveries succeed.</item>
/// <item>Each module's outbox is read with that module's own DbContext, in its own schema.</item>
/// <item>One host process reads the outboxes. Two hosts on one database can each deliver an event; the inbox keeps the
/// data correct.</item>
/// </list>
/// </remarks>
internal sealed class ModuleOutboxSender(
    IServiceScopeFactory scopes,
    IEnumerable<ModuleDatabase> databases,
    ModuleOutboxSignal signal,
    IModuleEventSerializer serializer,
    IOptionsMonitor<EventsOptions> options,
    TimeProvider time,
    IHostApplicationLifetime lifetime,
    ILogger<ModuleOutboxSender> logger) : BackgroundService
{
    internal const int BatchSize = 100;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(10);

    private readonly List<ModuleDatabase> _durable = databases.Where(database => database.DurableEvents).ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_durable.Count == 0)
        {
            return;
        }
        var schemas = string.Join(", ", _durable.Select(database => database.Schema));
        if (!options.CurrentValue.Outbox.Enabled)
        {
            logger.LogWarning("The module outbox sender is off (Events:Outbox:Enabled). The durable events of {Schemas} " +
                "stay in their outbox", schemas);
            return;
        }
        // Wolverine and the database migrations are ready only when the host has started.
        if (!await HostStartedAsync(stoppingToken))
        {
            return;
        }
        logger.LogInformation("The module outbox sender delivers the durable events of {Schemas}", schemas);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendAllAsync(stoppingToken);
                await signal.WaitAsync(options.CurrentValue.Outbox.PollInterval, stoppingToken);
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                // The host stops. An event in delivery stays in the outbox for the next start.
                return;
            }
        }
    }

    /// <summary>Delivers the waiting events of every durable module.</summary>
    internal async Task SendAllAsync(CancellationToken cancellationToken)
    {
        foreach (var database in _durable)
        {
            try
            {
                int read;
                do
                {
                    read = await SendBatchAsync(database, cancellationToken);
                }
                while (read == BatchSize);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "The module outbox sender could not read the outbox of {Schema}. It tries again later",
                    database.Schema);
            }
        }
    }

    /// <summary>Delivers up to <see cref="BatchSize"/> events of one module. Returns how many rows it read.</summary>
    private async Task<int> SendBatchAsync(ModuleDatabase database, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = (DbContext)scope.ServiceProvider.GetRequiredService(database.ContextType);
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
        var settings = options.CurrentValue.Outbox;
        var maxAttempts = settings.MaxAttempts;
        var now = time.GetUtcNow();

        var batch = await db.Set<OutboxMessage>()
            .Where(message => message.Attempts < maxAttempts && message.NextAttemptAt <= now)
            .OrderBy(message => message.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in batch)
        {
            try
            {
                var @event = serializer.Deserialize(message.EventType, message.Payload);
                await publisher.DeliverAsync(@event, cancellationToken);
                db.Remove(message);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Failed(database, message, ex, settings);
            }
            // One save per event: a stop after it does not deliver this event again. The delivery happened, so the
            // save runs also when the host stops now.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        return batch.Count;
    }

    private void Failed(ModuleDatabase database, OutboxMessage message, Exception ex, OutboxOptions settings)
    {
        message.Attempts++;
        var error = ex.GetBaseException().Message;
        message.LastError = error.Length > OutboxMessage.MaxErrorLength ? error[..OutboxMessage.MaxErrorLength] : error;
        message.NextAttemptAt = time.GetUtcNow() + RetryDelay(settings.RetryDelay, message.Attempts);

        if (message.Attempts >= settings.MaxAttempts)
        {
            logger.LogError(ex, "The event {EventType} (outbox row {Id} of {Schema}) failed {Attempts} times. The sender " +
                "stops: the row stays in the outbox with its last error", message.EventType, message.Id, database.Schema,
                message.Attempts);
        }
        else
        {
            logger.LogWarning(ex, "The event {EventType} (outbox row {Id} of {Schema}) failed. Try {Attempts} of " +
                "{MaxAttempts}; the next try is after {NextAttemptAt}", message.EventType, message.Id, database.Schema,
                message.Attempts, settings.MaxAttempts, message.NextAttemptAt);
        }
    }

    /// <summary>
    /// The first delay, doubled for each failure after the first, up to 10 minutes. A first delay of more than 10
    /// minutes stays as it is.
    /// </summary>
    internal static TimeSpan RetryDelay(TimeSpan first, int attempts)
    {
        var limit = first > MaxRetryDelay ? first : MaxRetryDelay;
        var delay = first;
        for (var i = 1; i < attempts && delay < limit; i++)
        {
            delay *= 2;
        }
        return delay < limit ? delay : limit;
    }

    private async Task<bool> HostStartedAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var onStarted = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await using var onStopping = stoppingToken.Register(() => started.TrySetCanceled(stoppingToken));
        try
        {
            await started.Task;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
