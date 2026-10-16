using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Inbox.Application.Mail;

/// <summary>The state of the mailbox reads, for the alert (T102). In memory: a restart starts it again.</summary>
public sealed class InboxMailState
{
    private readonly Lock _lock = new();

    public DateTimeOffset? LastSuccessAt { get; private set; }
    public DateTimeOffset? FailingSince { get; private set; }
    public string? LastError { get; private set; }

    public void Succeeded(DateTimeOffset now)
    {
        lock (_lock)
        {
            LastSuccessAt = now;
            FailingSince = null;
            LastError = null;
        }
    }

    public void Failed(DateTimeOffset now, string error)
    {
        lock (_lock)
        {
            FailingSince ??= now;
            LastError = error;
        }
    }
}

/// <summary>Reads the Inbox mailbox every <see cref="InboxMailOptions.PollInterval"/>, when it is on (T102).</summary>
public sealed class InboxMailWorker(
    IServiceScopeFactory scopes,
    IOptionsMonitor<InboxMailOptions> options,
    InboxMailState state,
    TimeProvider time,
    ILogger<InboxMailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.Enabled)
        {
            logger.LogInformation("Inbox: the mailbox is off (Inbox:Mail:Enabled)");
            return;
        }
        logger.LogInformation("Inbox: reading {User} on {Host} every {Interval}", options.CurrentValue.Username, options.CurrentValue.Host,
            options.CurrentValue.PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var reader = scope.ServiceProvider.GetRequiredService<InboxMailReader>();
                // Again at once while a read was full: a long list of new emails goes in batches.
                while ((await reader.ReadAsync(stoppingToken)) is var result &&
                       result.Notes + result.Ignored + result.Duplicates >= InboxMailReader.BatchSize)
                {
                }
                state.Succeeded(time.GetUtcNow());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                state.Failed(time.GetUtcNow(), exception.Message);
                logger.LogWarning(exception, "Inbox: the mailbox could not be read. It tries again in {Interval}", options.CurrentValue.PollInterval);
            }
            try
            {
                await Task.Delay(options.CurrentValue.PollInterval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
