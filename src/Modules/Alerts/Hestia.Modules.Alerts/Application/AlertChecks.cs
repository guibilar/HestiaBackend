using Hestia.Modules.Alerts.Contracts;

namespace Hestia.Modules.Alerts.Application;

/// <summary>The checks that other modules ask for (<see cref="IAlertChecks"/>), for example in a consumer of their events.</summary>
public sealed class AlertChecks(AlertsRunner runner) : IAlertChecks
{
    public Task CheckAsync(string source, string? target = null, AlertTrigger? trigger = null, CancellationToken cancellationToken = default) =>
        runner.RunAsync(source, target, trigger, cancellationToken);

    public void RequestCheck(string? source = null) => _ = runner.RequestAsync(source);
}
