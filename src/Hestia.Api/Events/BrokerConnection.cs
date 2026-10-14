using Hestia.Contracts.Finance;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Wolverine.Configuration;
using Endpoint = Wolverine.Configuration.Endpoint;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;

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
/// </summary>
internal sealed class BrokerReconnector(
    IWolverineRuntime runtime,
    IOptionsMonitor<EventsOptions> options,
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
            try
            {
                await CheckAsync(stoppingToken);
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
                await Task.Delay(options.CurrentValue.Broker.ReconnectInterval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One check: connect when the transport never connected, then start the listeners that need it.</summary>
    internal async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (FinanceBroker.Transport(runtime, options.CurrentValue) is not { } transport)
        {
            return;
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
                return;
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
