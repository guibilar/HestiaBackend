namespace Hestia.Modules.Alerts.Contracts;

/// <summary>
/// The alert rules of one module (T15). A module that has "Needs attention" items implements it, and registers it in
/// its <c>AddServices</c> as a scoped service: <c>services.AddScoped&lt;IAlertSource, DocumentExpiryAlerts&gt;()</c>.
/// </summary>
/// <remarks>
/// <para>The Alerts module runs each source in the background, never in the request of a user: on a timer
/// (<c>Alerts:CheckInterval</c>), when Today or <c>GET /api/alerts</c> asks for fresh alerts, and when a module asks for a
/// check after an event (<see cref="IAlertChecks"/>). The Alerts module then stores the result: a new key raises an
/// alert, a changed text or value updates it, and a key that the check covers but did not give resolves it.</para>
/// <para>The check states the whole truth for the keys that it covers (<see cref="AlertCheckResult.Covered"/>). When a
/// part of the data does not load, leave its key prefixes out of <c>Covered</c>: its alerts then stay as they are.</para>
/// </remarks>
public interface IAlertSource
{
    /// <summary>
    /// A stable name, <c>&lt;Module&gt;.&lt;Rules&gt;</c>, for example <c>Documents.Expiry</c>. The alerts of the source
    /// carry it. Do not change it later: the open alerts of an old name stay open.
    /// </summary>
    string Name { get; }

    /// <summary>The module of the alerts, for example <c>Documents</c>. Each alert key starts with its lower-case name and a dot.</summary>
    string Module { get; }

    /// <summary>The place of the module on Today: lower first. Money is 0 (D11). Other modules use 100 or more.</summary>
    int Order { get; }

    /// <summary>Runs the rules and returns the alerts that hold now.</summary>
    Task<AlertCheckResult> CheckAsync(AlertCheckContext context, CancellationToken cancellationToken);
}

/// <param name="Date">The day, in the time zone of the host (<c>Alerts:TimeZone</c>).</param>
/// <param name="TimeZone">That time zone, to change a moment (UTC) to a local day.</param>
/// <param name="Now">The moment of the check.</param>
/// <param name="Target">
/// Null: check everything. Else the id of one thing (for example a document id) that an event changed: check only its
/// rules, and cover only its keys.
/// </param>
public sealed record AlertCheckContext(DateOnly Date, TimeZoneInfo TimeZone, DateTimeOffset Now, string? Target = null);

/// <param name="Alerts">The alerts that hold now, for the covered keys.</param>
/// <param name="Covered">
/// The key prefixes that the check looked at completely, for example <c>documents.expiring.</c>. An open alert of the
/// source whose key starts with one of them, and that is not in <paramref name="Alerts"/>, is resolved.
/// </param>
/// <param name="Complete">False when some data did not load. Today then shows the module as Partial.</param>
public sealed record AlertCheckResult(IReadOnlyList<AlertCandidate> Alerts, IReadOnlyList<string> Covered, bool Complete = true);

/// <summary>One alert that a rule found. The fields are the fields of a Today "Needs attention" card.</summary>
/// <param name="Key">
/// Stable for the same condition, so a check makes one alert, not many: the Today card id, for example
/// <c>documents.expiring.&lt;id&gt;</c>. It starts with the lower-case module name and a dot.
/// </param>
/// <param name="Kind">What the alert is about, inside its module, for example <c>expiring</c>. Clients pick the icon with it.</param>
/// <param name="Title">A short name of its kind, for example "Document expires soon".</param>
/// <param name="Text">The sentence, with placeholders such as <c>{document}</c>. The clients fill them from <paramref name="Values"/>.</param>
/// <param name="Values">The value of each placeholder.</param>
/// <param name="Severity">How the alert looks.</param>
/// <param name="Link">A client route that opens the matching page, for example <c>/documents/&lt;id&gt;</c>. Null for none.</param>
/// <param name="SortKey">The place of the alert inside its module on Today: ordinal order of this text.</param>
public sealed record AlertCandidate(
    string Key,
    string Kind,
    string Title,
    string Text,
    IReadOnlyDictionary<string, AlertValue> Values,
    AlertSeverity Severity,
    string? Link,
    string SortKey);
