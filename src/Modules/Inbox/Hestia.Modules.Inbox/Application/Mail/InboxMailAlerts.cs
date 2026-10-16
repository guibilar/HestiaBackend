using Hestia.Modules.Alerts.Contracts;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Inbox.Application.Mail;

/// <summary>
/// The alert rule of the Inbox mailbox (T102): <c>inbox.mail-failing</c> (Warning) when the reads have failed for longer
/// than <see cref="InboxMailOptions.AlertAfter"/>, for example after a changed password.
/// </summary>
public sealed class InboxMailAlerts(InboxMailState state, IOptionsMonitor<InboxMailOptions> options) : IAlertSource
{
    public const string Key = "inbox.mail-failing";

    public string Name => "Inbox.Mail";

    public string Module => "Inbox";

    public int Order => 150;

    public Task<AlertCheckResult> CheckAsync(AlertCheckContext context, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var alerts = new List<AlertCandidate>();
        if (settings.Enabled && state.FailingSince is { } since && context.Now - since >= settings.AlertAfter)
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(since, context.TimeZone).DateTime);
            alerts.Add(new AlertCandidate(Key, "mail-failing", "Cannot read the Inbox mailbox",
                "Hestia cannot read the mailbox {mailbox} since {date}: {error}. Check the password (an app password) in the .env.",
                new Dictionary<string, AlertValue>
                {
                    ["mailbox"] = AlertValue.Plain(settings.Username ?? string.Empty),
                    ["date"] = AlertValue.Day(day),
                    ["error"] = AlertValue.Plain(state.LastError ?? "unknown error")
                },
                AlertSeverity.Warning, Link: "/inbox", SortKey: "0"));
        }
        return Task.FromResult(new AlertCheckResult(alerts, [Key]));
    }
}
