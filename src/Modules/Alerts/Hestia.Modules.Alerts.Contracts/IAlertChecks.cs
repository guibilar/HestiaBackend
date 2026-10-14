namespace Hestia.Modules.Alerts.Contracts;

/// <summary>
/// How a module asks the Alerts module to run its rules again, for example in a consumer of its own events. The Alerts
/// module registers the implementation (a singleton).
/// </summary>
public interface IAlertChecks
{
    /// <summary>
    /// Runs one source now and stores its result. Use it in background code (an event consumer), not in a request.
    /// </summary>
    /// <param name="source">The <see cref="IAlertSource.Name"/>.</param>
    /// <param name="target">Null for everything, or the id of the one thing that changed (see <see cref="AlertCheckContext.Target"/>).</param>
    /// <param name="trigger">
    /// The event that asked for the check, or null. With it, the check is idempotent (T36): the Alerts module stores the
    /// event in its inbox with the result, and does nothing for an event that it handled already.
    /// </param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <exception cref="ArgumentException">No source has this name.</exception>
    Task CheckAsync(string source, string? target = null, AlertTrigger? trigger = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks for a check of one source (or of all sources, with null) soon, in the background. It does not wait. Several
    /// requests before the check starts give one check.
    /// </summary>
    void RequestCheck(string? source = null);
}

/// <summary>The event that asks for a check (for the inbox of the Alerts module).</summary>
/// <param name="EventId">The <c>EventId</c> of the event.</param>
/// <param name="Consumer">The stable name of the consumer, <c>&lt;Module&gt;.&lt;Class&gt;</c>. Do not change it later.</param>
public sealed record AlertTrigger(Guid EventId, string Consumer);
