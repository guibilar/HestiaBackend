using System.Reflection;
using Hestia.Contracts;
using Hestia.Contracts.Finance;
using Hestia.Platform.Events;
using Hestia.Platform.Finance;
using Hestia.Platform.Health;
using Hestia.Platform.Modules;
using Hestia.Platform.Persistence;
using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Configuration;
using Wolverine.RabbitMQ;
using Wolverine.RabbitMQ.Internal;

namespace Hestia.Api.Events;

/// <summary>The <c>Events</c> settings of the host.</summary>
public sealed class EventsOptions
{
    public const string SectionName = "Events";

    /// <summary>
    /// The RabbitMQ broker that finance publishes its events to, for example <c>amqp://hestia:secret@rabbitmq:5672</c>.
    /// Empty: the host does not listen for finance events. The in-process module events work without it.
    /// </summary>
    public string? RabbitMq { get; set; }

    /// <summary>
    /// A debug consumer logs every event (finance and module events) at Information level. The host reads it for each
    /// event, so a change in appsettings.json takes effect without a restart.
    /// </summary>
    public bool LogAll { get; set; }

    /// <summary>The module outbox sender: durable module events (T38).</summary>
    public OutboxOptions Outbox { get; set; } = new();

    /// <summary>The connection to the broker: the start timeout and the reconnect (T37).</summary>
    public BrokerOptions Broker { get; set; } = new();
}

/// <summary>
/// Events in the host (D7), with Wolverine:
/// <list type="bullet">
/// <item>Module events: in process. A module raises them on its DbContext; the platform publishes them after the commit
/// (<see cref="ModuleEventsInterceptor"/>) to Wolverine local queues.</item>
/// <item>Finance events: from RabbitMQ, when <see cref="EventsOptions.RabbitMq"/> is set. Finance writes them to its
/// outbox and sends them to the exchange <see cref="FinanceEventTopology.Exchange"/>.</item>
/// </list>
/// A consumer is a Wolverine handler (a public class whose name ends in "Consumer", with a <c>Consume</c> method) in a
/// module project. It does not know which path an event used.
/// </summary>
public static class HestiaEvents
{
    public static WebApplicationBuilder AddHestiaEvents(this WebApplicationBuilder builder, IReadOnlyList<IModule> modules)
    {
        builder.Services.AddOptions<EventsOptions>().BindConfiguration(EventsOptions.SectionName)
            .Validate(options => options.Outbox.PollInterval > TimeSpan.Zero && options.Outbox.RetryDelay > TimeSpan.Zero,
                $"{EventsOptions.SectionName}:Outbox:PollInterval and RetryDelay must be more than zero.")
            .Validate(options => options.Outbox.MaxAttempts >= 1,
                $"{EventsOptions.SectionName}:Outbox:MaxAttempts must be 1 or more.")
            .Validate(options => options.Outbox.WarnAfter > TimeSpan.Zero,
                $"{EventsOptions.SectionName}:Outbox:WarnAfter must be more than zero.")
            .Validate(options => options.Broker.StartTimeout >= TimeSpan.Zero && options.Broker.ReconnectInterval > TimeSpan.Zero,
                $"{EventsOptions.SectionName}:Broker:StartTimeout must be zero or more, and ReconnectInterval more than zero.")
            .ValidateOnStart();
        // Before the modules register their DbContexts: the platform keeps these (TryAdd).
        builder.Services.AddScoped<IEventPublisher, WolverineEventPublisher>();
        // Durable module events (T38): the outbox format, and the sender that delivers the rows.
        builder.Services.AddSingleton<IModuleEventSerializer>(new ModuleEventSerializer(HestiaEvents.ModuleEventTypes(modules)));
        builder.Services.AddHostedService<ModuleOutboxSender>();
        // T37: the event health in /health/ready (Degraded, so the apps still work), the reconnect to the broker, and
        // the command that retries the stuck outbox rows.
        builder.Services.AddHealthChecks()
            .AddCheck<BrokerHealthCheck>(BrokerHealthCheck.Name, tags: [HealthTags.Ready])
            .AddCheck<ModuleOutboxHealthCheck>(ModuleOutboxHealthCheck.Name, failureStatus: HealthStatus.Degraded, tags: [HealthTags.Ready]);
        builder.Services.AddHostedService<BrokerReconnector>();
        builder.Services.AddSingleton<IModuleCommand, RetryOutboxCommand>();
        // The setup reads the Events settings when the host starts (an extension from the container), so it sees the
        // final configuration, test settings included.
        builder.Services.AddSingleton<IWolverineExtension>(services =>
            new HestiaEventsSetup(services.GetRequiredService<IConfiguration>(), modules,
                services.GetServices<ModuleDatabase>().ToList()));
        builder.UseWolverine(_ => { });
        return builder;
    }

    /// <summary>The finance events and the events in each module's <c>.Contracts</c> assembly.</summary>
    public static IReadOnlyList<Type> EventTypes(IEnumerable<IModule> modules) =>
        FinanceEventTopology.EventTypes.Concat(ModuleEventTypes(modules))
            .Distinct()
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

    /// <summary>The events in each module's <c>.Contracts</c> assembly: the events that a module can raise.</summary>
    public static IReadOnlyList<Type> ModuleEventTypes(IEnumerable<IModule> modules)
    {
        var types = new List<Type>();
        foreach (var module in modules)
        {
            var name = module.GetType().Assembly.GetName().Name + ".Contracts";
            var contracts = Assembly.Load(new AssemblyName(name));
            types.AddRange(contracts.GetExportedTypes().Where(type =>
                type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } &&
                typeof(IIntegrationEvent).IsAssignableFrom(type)));
        }
        return types.Distinct().OrderBy(type => type.FullName, StringComparer.Ordinal).ToList();
    }
}

/// <summary>The Wolverine setup of the host: handler discovery, the debug consumer, and the RabbitMQ listener.</summary>
internal sealed class HestiaEventsSetup(
    IConfiguration configuration,
    IReadOnlyList<IModule> modules,
    IReadOnlyList<ModuleDatabase> databases) : IWolverineExtension
{
    public void Configure(WolverineOptions wolverine)
    {
        var options = configuration.GetSection(EventsOptions.SectionName).Get<EventsOptions>() ?? new EventsOptions();

        // Explicit, so a test host finds the same handlers (Wolverine otherwise guesses from the call stack).
        wolverine.ApplicationAssembly = typeof(HestiaEvents).Assembly;
        foreach (var assembly in modules.Select(module => module.GetType().Assembly).Distinct())
        {
            wolverine.Discovery.IncludeAssembly(assembly);
        }

        // The shared finance client is a typed HttpClient: a lambda registration that Wolverine cannot build in its
        // generated code. Wolverine 6 refuses service location by default, so a consumer that takes IFinanceClient
        // (with AsSystem, T42) would fail. Allow it for this one service.
        wolverine.CodeGeneration.AlwaysUseServiceLocationFor<IFinanceClient>();
        // The same for each module DbContext (AddDbContext is a lambda registration too): an idempotent consumer takes
        // its module's DbContext (T36).
        foreach (var database in databases)
        {
            wolverine.CodeGeneration.AlwaysUseServiceLocationFor(database.ContextType);
        }

        ConsumerRetries.Apply(wolverine);

        // Always registered, and quiet unless LogAll is on: the setting can change without a restart, and the set of
        // consumers does not depend on it.
        foreach (var eventType in HestiaEvents.EventTypes(modules))
        {
            wolverine.Discovery.IncludeType(typeof(EventLog<>).MakeGenericType(eventType));
        }

        if (!string.IsNullOrWhiteSpace(options.RabbitMq))
        {
            // Declares the exchange, the queue, and the binding when they do not exist (idempotent).
            wolverine.UseRabbitMq(ParseRabbitMq(options.RabbitMq))
                .AutoProvision()
                // No request/reply over the broker. Its system queue is a listener, and a listener whose broker is down
                // stops the start of the host (T37).
                .DisableSystemRequestReplyQueueDeclaration()
                .DeclareExchange(FinanceEventTopology.Exchange, exchange =>
                {
                    exchange.ExchangeType = ExchangeType.Fanout;
                    exchange.BindQueue(FinanceEventTopology.HostQueue);
                });
            // Inline: the broker removes a message only after its consumers finished. A consumer that fails gets the
            // retries of ConsumerRetries; after them, or at once for other errors, the message goes to the
            // dead-letter queue of the broker (wolverine-dead-letter-queue).
            // T37: the queue is declared, but it is not a Wolverine listener: Wolverine starts listeners at the start of
            // the host, and a broker that is down then stops the start. BrokerReconnector starts the listener as soon
            // as the host has started, and again after a broker outage.
            wolverine.Transports.GetOrCreate<RabbitMqTransport>().Queues[FinanceEventTopology.HostQueue].Mode = EndpointMode.Inline;

            // The host is the gateway: a broker that is down when the host starts must not stop it. The host then
            // starts without the finance events and logs the error. BrokerReconnector connects when the broker is
            // back (T37). A short start timeout (Wolverine's default is 2 minutes), so the host answers requests soon.
            wolverine.ResourceMigrationFailureMode = ResourceMigrationFailureMode.ContinueOnFailures;
            wolverine.BrokerInitializationTimeout = options.Broker.StartTimeout;
        }
    }

    private static Uri ParseRabbitMq(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("amqp" or "amqps"))
        {
            throw new InvalidOperationException(
                $"{EventsOptions.SectionName}:RabbitMq must be an amqp:// or amqps:// address, for example amqp://user:password@rabbitmq:5672.");
        }
        return uri;
    }
}

/// <summary>
/// The retry policy for consumers (T52). Without a policy, Wolverine 6 tries a message once: the first failure moves it to
/// the error queue (the RabbitMQ dead-letter queue, or nothing for a local queue, because the host has no Wolverine
/// message storage).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A short database failure gets <see cref="Delays"/>.Length more tries in the same delivery, after these
/// delays: a failed save (<see cref="DbUpdateException"/>, also the inbox key of a duplicate delivery at the same time),
/// a transient PostgreSQL error, or the end of the retries of the EF Core execution strategy.</item>
/// <item>After the last try, and at once for all other errors (a bug does not go away with a retry), Wolverine moves the
/// message to the error queue and logs an error ("was moved to the error queue"). RabbitMQ: the dead-letter queue of the
/// broker. A local queue (module events): the message is dropped after the error log. The module outbox sender
/// (<c>InvokeAsync</c>) gets the exception instead, and keeps the row for its own later try (T38).</item>
/// <item>A retry runs all consumers of the event again (Wolverine combines them in one handler). So every consumer that
/// changes data uses the inbox (T36).</item>
/// </list>
/// </remarks>
public static class ConsumerRetries
{
    /// <summary>The waits before the second, third, and fourth try.</summary>
    public static readonly TimeSpan[] Delays =
        [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2)];

    /// <summary>True for an error that a later try can fix.</summary>
    public static bool IsTransient(Exception exception) => exception switch
    {
        DbUpdateException or RetryLimitExceededException => true,
        NpgsqlException npgsql => npgsql.IsTransient,
        _ => false
    };

    internal static void Apply(WolverineOptions wolverine) =>
        wolverine.Policies.OnException(IsTransient, "Transient database error (T52)")
            .RetryWithCooldown(Delays)
            .Then.MoveToErrorQueue();
}

/// <summary>Publishes module events to their in-process consumers (Wolverine local queues).</summary>
/// <remarks>
/// A consumer has no signed-in user (D7). The publish runs without the execution context of the caller, so the HTTP
/// request of the change (<c>IHttpContextAccessor</c>) never flows into a consumer. A consumer then sees no request,
/// and can use the system finance client (<c>IFinanceClient.AsSystem</c>, T42), as a consumer of a finance event can.
/// </remarks>
internal sealed class WolverineEventPublisher(IMessageBus bus) : IEventPublisher
{
    /// <summary>How long one outbox delivery can take (all consumers of one event).</summary>
    internal static readonly TimeSpan DeliveryTimeout = TimeSpan.FromMinutes(1);

    public ValueTask PublishAsync(object @event, CancellationToken cancellationToken = default)
    {
        if (ExecutionContext.IsFlowSuppressed())
        {
            return bus.PublishAsync(@event);
        }
        Task publish;
        using (ExecutionContext.SuppressFlow())
        {
            publish = Task.Run(() => bus.PublishAsync(@event).AsTask(), CancellationToken.None);
        }
        return new ValueTask(publish);
    }

    /// <summary>
    /// Runs all consumers of the event in this call (Wolverine <c>InvokeAsync</c>), for the outbox sender (T38). The
    /// sender runs outside any HTTP request, so the consumers see no request here either.
    /// </summary>
    public Task DeliverAsync(object @event, CancellationToken cancellationToken = default) =>
        bus.InvokeAsync(@event, cancellationToken, DeliveryTimeout);
}

/// <summary>
/// The debug consumer (<c>Events:LogAll</c>): one handler per event type, which logs the event with all its values when
/// the setting is on. It is a consumer like any other, so it shows what consumers receive.
/// </summary>
public sealed class EventLog<TEvent>(IOptionsMonitor<EventsOptions> options, ILogger<EventLog<TEvent>> logger) where TEvent : class
{
    public void Consume(TEvent message)
    {
        if (options.CurrentValue.LogAll)
        {
            logger.LogInformation("Event received: {Event}", message);
        }
    }
}
