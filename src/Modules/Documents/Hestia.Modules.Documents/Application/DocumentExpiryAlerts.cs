using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Documents.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Documents.Application;

/// <summary>
/// The Documents alert rules (T15; until T15 a Today provider): a "Needs attention" item for each document that expires
/// in the next 30 days (<c>Documents:Expiry:SoonDays</c>), and for each document that expired in the last 7 days
/// (<c>Documents:Expiry:ExpiredDays</c>). Soonest first. Each item opens the document.
/// </summary>
/// <remarks>
/// The Alerts module checks all documents on its timer (a document starts to expire because time passes). After a
/// document event, <see cref="DocumentAlertsConsumer"/> checks only that document (<see cref="AlertCheckContext.Target"/>).
/// </remarks>
public sealed class DocumentExpiryAlerts(DocumentsDbContext db, IOptions<DocumentsOptions> options) : IAlertSource
{
    public const string SourceName = "Documents.Expiry";
    public const string ModuleName = "Documents";
    public const string ExpiringPrefix = "documents.expiring.";
    public const string ExpiredPrefix = "documents.expired.";

    public string Name => SourceName;

    public string Module => ModuleName;

    public int Order => 100;

    public async Task<AlertCheckResult> CheckAsync(AlertCheckContext context, CancellationToken cancellationToken)
    {
        var expiry = options.Value.Expiry;
        var from = context.Date.AddDays(-expiry.ExpiredDays);
        var to = context.Date.AddDays(expiry.SoonDays);
        var documents = db.Documents.AsNoTracking().Where(d => d.ExpiresOn >= from && d.ExpiresOn <= to);

        IReadOnlyList<string> covered;
        if (context.Target is null)
        {
            covered = [ExpiringPrefix, ExpiredPrefix];
        }
        else if (Guid.TryParse(context.Target, out var id))
        {
            // One document: a new or changed expiry date, or a delete (then nothing is found, and its alerts resolve).
            documents = documents.Where(d => d.Id == id);
            covered = [$"{ExpiringPrefix}{id}", $"{ExpiredPrefix}{id}"];
        }
        else
        {
            return new AlertCheckResult([], []);
        }

        var found = await documents
            .Select(d => new { d.Id, d.Title, ExpiresOn = d.ExpiresOn!.Value })
            .ToListAsync(cancellationToken);
        var alerts = found.Select(d => Alert(d.Id, d.Title, d.ExpiresOn, context.Date)).ToList();
        return new AlertCheckResult(alerts, covered);
    }

    /// <summary>The alert of one document that expires on <paramref name="expiresOn"/>, seen on <paramref name="today"/>.</summary>
    public static AlertCandidate Alert(Guid id, string title, DateOnly expiresOn, DateOnly today)
    {
        var days = expiresOn.DayNumber - today.DayNumber;
        var values = new Dictionary<string, AlertValue>
        {
            ["document"] = AlertValue.Plain(title),
            ["date"] = AlertValue.Day(expiresOn),
            ["days"] = AlertValue.Count(Math.Abs(days))
        };
        var link = $"/documents/{id}";
        // Soonest first; on the same day by title, then by id.
        var sortKey = $"{expiresOn:yyyy-MM-dd}|{title}|{id}";
        return days < 0
            ? new AlertCandidate($"{ExpiredPrefix}{id}", "expired", "Document expired", "{document} expired on {date}",
                values, AlertSeverity.Bad, link, sortKey)
            : new AlertCandidate($"{ExpiringPrefix}{id}", "expiring", "Document expires soon",
                days == 0 ? "{document} expires today" : "{document} expires on {date}", values,
                days <= 7 ? AlertSeverity.Bad : AlertSeverity.Warning, link, sortKey);
    }
}
