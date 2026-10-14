using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.Home.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.Home.Application;

/// <summary>
/// The Home alert rules (T22): a "Needs attention" item (<c>home.warranty-ending.&lt;id&gt;</c>, Neutral) for each item
/// whose warranty ends today or in the next 30 days (<c>Home:WarrantySoonDays</c>). Soonest first. Each item opens the
/// item.
/// </summary>
/// <remarks>
/// <para>No double alert: an item is left out when one of its documents (from either side of the link) expires on the
/// same day as the warranty. The Documents module warns about that document already.</para>
/// <para>The Alerts module checks all items on its timer (a warranty starts to end because time passes). After an item
/// event, <see cref="HomeAlertsConsumer"/> checks only that item (<see cref="AlertCheckContext.Target"/>). "Today" is the
/// day of the check in <c>Home:TimeZone</c>.</para>
/// </remarks>
public sealed class WarrantyAlerts(HomeDbContext db, HomeDays days, ItemRelations relations) : IAlertSource
{
    public const string SourceName = "Home.Warranty";
    public const string ModuleName = "Home";
    public const string EndingPrefix = "home.warranty-ending.";

    public string Name => SourceName;

    public string Module => ModuleName;

    /// <summary>After Documents (100) and Tasks (110).</summary>
    public int Order => 120;

    public async Task<AlertCheckResult> CheckAsync(AlertCheckContext context, CancellationToken cancellationToken)
    {
        var today = days.DayOf(context.Now);
        var soon = days.SoonUntil(today);
        var items = db.Items.AsNoTracking().Where(i => i.WarrantyUntil >= today && i.WarrantyUntil <= soon);

        IReadOnlyList<string> covered;
        if (context.Target is null)
        {
            covered = [EndingPrefix];
        }
        else if (Guid.TryParse(context.Target, out var id))
        {
            // One item: a new or changed warranty, a new link, or a delete (then nothing is found, and its alert resolves).
            items = items.Where(i => i.Id == id);
            covered = [$"{EndingPrefix}{id}"];
        }
        else
        {
            return new AlertCheckResult([], []);
        }

        var found = await items
            .Select(i => new
            {
                i.Id,
                i.Name,
                WarrantyUntil = i.WarrantyUntil!.Value,
                Links = i.Links.Select(l => new ItemLinkDto(l.Id, l.Kind, l.TargetId, l.CreatedAt)).ToList()
            })
            .ToListAsync(cancellationToken);
        var alerts = new List<AlertCandidate>();
        foreach (var item in found)
        {
            var documents = await relations.DocumentsAsync(item.Id, item.Links, cancellationToken);
            if (documents.Any(d => d.ExpiresOn == item.WarrantyUntil))
            {
                continue; // The document alert (Documents.Expiry) covers it.
            }
            alerts.Add(Alert(item.Id, item.Name, item.WarrantyUntil, today));
        }
        return new AlertCheckResult(alerts, covered);
    }

    /// <summary>The alert of one item whose warranty ends on <paramref name="until"/>, seen on <paramref name="today"/>.</summary>
    public static AlertCandidate Alert(Guid id, string name, DateOnly until, DateOnly today)
    {
        var days = until.DayNumber - today.DayNumber;
        var values = new Dictionary<string, AlertValue>
        {
            ["item"] = AlertValue.Plain(name),
            ["date"] = AlertValue.Day(until),
            ["days"] = AlertValue.Count(days)
        };
        return new AlertCandidate($"{EndingPrefix}{id}", "warranty-ending", "Warranty ends soon",
            days == 0 ? "The warranty of {item} ends today" : "The warranty of {item} ends on {date}", values,
            AlertSeverity.Neutral, $"/home/items/{id}", $"{until:yyyy-MM-dd}|{name}|{id}");
    }
}
