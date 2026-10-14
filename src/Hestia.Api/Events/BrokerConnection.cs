using Hestia.Contracts.Finance;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Endpoint = Wolverine.Configuration.Endpoint;

namespace Hestia.Api.Events;

/// <summary>The connection to the RabbitMQ broker of the finance events (<c>Events:Broker</c>, T37).</summary>
public sealed class BrokerOptions
{
    /// <summary>
    /// How long the host tries to reach the broker when it starts. After that, it starts without the finance events,
    /// and <see cref="BrokerReconnector"/> tries again in the background. Wolverine's own default is 2 minutes, and the
    /// host does not answer requests during that time.
    /// </summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often <see cref="BrokerReconnector"/> looks at the connection and the listener.</summary>
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// After this time without a broker connection, the alert <c>system.broker-down</c> shows on Today (T80). A short
    /// outage, for example a broker update, gives no alert.
    /// </summary>
    public TimeSpan AlertAfter { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Since when the host has no working connection to the broker of the finance events (T80).
/// <see cref="BrokerReconnector"/> reports the state at each of its checks; the alert source
/// <see cref="SystemHealthAlerts"/> reads it. The state is in memory: a restart of the host starts it again.
/// </summary>
public sealed class BrokerConnectionState(TimeProvider time)
{
    // UTC ticks of the first report "down" after the last report "up"; 0 while the host has a connection.
    private long _downSinceTicks;
    private int _reported;

    /// <summary>True after the first report. Before it, the host does not know the state of the broker.</summary>
    public bool Known => Volatile.Read(ref _reported) == 1;

    /// <summary>Since when the host has no connection, or null when it has one (or does not know yet).</summary>
    public DateTimeOffset? DownSince
    {
        get
        {
            var ticks = Interlocked.Read(ref _downSinceTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Records the result of one check. A report "down" keeps the time of the first one.</summary>
    public void Report(bool connected)
    {
        if (connected)
        {
            Interlocked.Exchange(ref _downSinceTicks, 0);
        }
        else
        {
            Interlocked.CompareExchange(ref _downSinceTicks, time.GetUtcNow().UtcTicks, 0);
        }
        Volatile.Write(ref _reported, 1);
    }
}

/// <summary>The RabbitMQ transport of the host, and the state of its listener for the finance events.</summary>
internal static class FinanceBroker
{
    /// <summary>The RabbitMQ transport, or null when the host does not listen for finance events.</summary>
    public static RabbitMqTransport? Transport(IWolverineRuntime runtime, EventsOptions options) =>
        string.IsNullOrWhiteSpace(options.RabbitMq) ? null : runtime.Options.Transports.OfType<RabbitMqTransport>().FirstOrDefault();

    /// <summary>
    /// The endpoints that the host listens to on the broker: the queue of the finance events. It is not a Wolverine
    /// listener (see <see cref="HestiaEventsSetup"/>), so <see cref="BrokerReconnector"/> starts it.
    /// </summary>
    public static IEnumerable<Endpoint> Listeners(RabbitMqTransport transport) =>
        [transport.Queues[FinanceEventTopology.HostQueue]];

    /// <summary>True when the listener of the endpoint takes messages.</summary>
    public static bool Accepting(IWolverineRuntime runtime, Endpoint endpoint) =>
        runtime.Endpoints.FindListeningAgent(endpoint.Uri)?.Status == ListeningStatus.Accepting;

    /// <summary>
    /// True when the listener needs a start: it never started (the broker was down when the host started), or it
    /// stopped. Not for a listener that Wolverine stopped for a short time on purpose (back pressure, a pause).
    /// </summary>
    public static bool NeedsStart(IWolverineRuntime runtime, Endpoint endpoint) =>
        runtime.Endpoints.FindListeningAgent(endpoint.Uri)?.Status is null or ListeningStatus.Stopped or ListeningStatus.Unknown;

    /// <summary>True when the transport has a connection: it connected once, at the start or later.</summary>
    public static async Task<bool> ConnectedOnceAsync(RabbitMqTransport transport, CancellationToken cancellationToken) =>
        (await transport.ProbeAsync(cancellationToken)).Status != BrokerHealthStatus.Unknown;

    /// <summary>
    /// True when the host gets the finance events: it has a connection to the broker, and its listener takes messages.
    /// The same rule as the readiness check <c>events-broker</c>.
    /// </summary>
    public static async Task<bool> WorkingAsync(IWolverineRuntime runtime, RabbitMqTransport transport, CancellationToken cancellationToken)
    {
        var probe = await transport.ProbeAsync(cancellationToken);
        return probe.Status is not (BrokerHealthStatus.Unknown or BrokerHealthStatus.Unhealthy) &&
            Listeners(transport).All(endpoint => Accepting(runtime, endpoint));
    }
}

/// <summary>
/// The readiness check <c>events-broker</c> (T37). <b>Degraded</b> (not Unhealthy, so <c>/health/ready</c> stays 200 and
/// the apps work) when the host has no connection to the RabbitMQ broker, or when its listener for the finance events
/// does not take messages. Finance keeps its events in its outbox meanwhile. Healthy when the host does not use a broker
/// (no <c>Events:RabbitMq</c>).
/// </summary>
public sealed class BrokerHealthCheck(IWolverineRuntime runtime, IOptionsMonitor<EventsOptions> options) : IHealthCheck
{
    public const string Name = "events-broker";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (FinanceBroker.Transport(runtime, options.CurrentValue) is not { } transport)
        {
            return HealthCheckResult.Healthy("The host does not use a broker (Events:RabbitMq is empty).");
        }

        var probe = await transport.ProbeAsync(cancellationToken);
        if (probe.Status is BrokerHealthStatus.Unknown or BrokerHealthStatus.Unhealthy)
        {
            return HealthCheckResult.Degraded(
                $"The host has no connection to the RabbitMQ broker ({probe.Status}). The finance events wait in the " +
                "finance outbox; the host connects again when the broker is back.");
        }

        var stopped = FinanceBroker.Listeners(transport).Where(endpoint => !FinanceBroker.Accepting(runtime, endpoint))
            .Select(endpoint => endpoint.EndpointName).ToList();
        return stopped.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded($"The host does not listen to {string.Join(", ", stopped)} on the RabbitMQ broker.");
    }
}

/// <summary>
/// Starts the listener of the finance events, and connects the RabbitMQ transport when the broker was down at the start
/// of the host (T37). Wolverine (with <c>ContinueOnFailures</c>) then starts the host without the transport and does
/// not try again. At once after the start, and then every <see cref="BrokerOptions.ReconnectInterval"/>, this service:
/// <list type="number">
/// <item>connects the transport, when it never connected, and declares the exchange, the queue, and the binding;</item>
/// <item>starts the listener when it never started or stopped. The queue is not a Wolverine listener, because
/// Wolverine starts its listeners at the start of the host, and a broker that is down then stops the start.</item>
/// </list>
/// A broker that goes down <i>after</i> a connection needs nothing from this service: the RabbitMQ client recovers the
/// connection itself, and Wolverine rebuilds its channels.
/// After each check, it reports the state of the connection to <see cref="BrokerConnectionState"/> (T80), for the
/// alert <c>system.broker-down</c>.
/// </summary>
internal sealed class BrokerReconnector(
    IWolverineRuntime runtime,
    IOptionsMonitor<EventsOptions> options,
    BrokerConnectionState state,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<BrokerReconnector> logger) : BackgroundService
{
    private bool _reportedDown;

    /// <summary>True after a late connection, until the exchange, the queue, and the binding are declared.</summary>
    private bool _needsSetup;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.CurrentValue.RabbitMq))
        {
            return;
        }
        if (!await HostStartedAsync(stoppingToken))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var reached = true;
            try
            {
                reached = await CheckAsync(stoppingToken);
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Events: the check of the RabbitMQ connection failed. It tries again in {Interval}",
                    options.CurrentValue.Broker.ReconnectInterval);
            }

            try
            {
                if (reached)
                {
                    await ReportAsync(stoppingToken);
                }
                else
                {
                    // The connect failed just now: no probe is necessary (a probe here also changes what the
                    // readiness check sees right after a failed connect).
                    state.Report(connected: false);
                }
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A probe that fails tells nothing about the connection. The state stays as it was.
                logger.LogDebug(ex, "Events: the probe of the RabbitMQ connection failed");
            }

            try
            {
                await Task.Delay(options.CurrentValue.Broker.ReconnectInterval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One check: connect when the transport never connected, then start the listeners that need it. False when the
    /// transport never connected and the connect failed now.
    /// </summary>
    internal async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        if (FinanceBroker.Transport(runtime, options.CurrentValue) is not { } transport)
        {
            return true;
        }

        if (!await FinanceBroker.ConnectedOnceAsync(transport, cancellationToken))
        {
            try
            {
                // One quick try. It throws while the broker is down, and leaves the transport as it was.
                await transport.ConnectAsync(runtime);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (!_reportedDown)
                {
                    _reportedDown = true;
                    logger.LogWarning("Events: the RabbitMQ broker is not reachable ({Error}). The host has no finance " +
                        "events until it is back; it tries again every {Interval}", ex.GetBaseException().Message,
                        options.CurrentValue.Broker.ReconnectInterval);
                }
                return false;
            }
            _reportedDown = false;
            _needsSetup = true;
        }

        if (_needsSetup)
        {
            // Declares the exchange, the queue, and the binding (AutoProvision), as a normal start does. On a failure,
            // the next check does it again.
            await transport.InitializeAsync(runtime);
            _needsSetup = false;
            logger.LogInformation("Events: connected to the RabbitMQ broker after the start of the host");
        }

        foreach (var endpoint in FinanceBroker.Listeners(transport).Where(endpoint => FinanceBroker.NeedsStart(runtime, endpoint)))
        {
            await runtime.Endpoints.StartListenerAsync(endpoint, cancellationToken);
            logger.LogInformation("Events: the host listens to the queue {Queue}", endpoint.EndpointName);
        }
        return true;
    }

    /// <summary>Reports whether the host gets the finance events now (T80).</summary>
    internal async Task ReportAsync(CancellationToken cancellationToken)
    {
        if (FinanceBroker.Transport(runtime, options.CurrentValue) is { } transport)
        {
            state.Report(await FinanceBroker.WorkingAsync(runtime, transport, cancellationToken));
        }
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
