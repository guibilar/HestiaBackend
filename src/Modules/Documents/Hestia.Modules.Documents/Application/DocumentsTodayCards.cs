using Hestia.Modules.Documents.Infrastructure;
using Hestia.Modules.Today.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Documents.Application;

/// <summary>
/// The Documents cards on Today: a "Needs attention" item for each document that expires in the next 30 days
/// (<c>Documents:Expiry:SoonDays</c>), and for each document that expired in the last 7 days
/// (<c>Documents:Expiry:ExpiredDays</c>). Soonest first. Each item opens the document.
/// </summary>
/// <remarks>
/// T15 (the Alerts module) is not merged yet, so the rules are here, as a Today provider. When the Alerts module comes,
/// these rules move to an Alerts source.
/// </remarks>
public sealed class DocumentsTodayCards(DocumentsDbContext db, IOptions<DocumentsOptions> options) : ITodayCardProvider
{
    public const string ModuleName = "Documents";

    public string Module => ModuleName;

    public int Order => 100;

    public async Task<TodayModuleCards> GetCardsAsync(TodayContext context, CancellationToken cancellationToken)
    {
        var expiry = options.Value.Expiry;
        var from = context.Date.AddDays(-expiry.ExpiredDays);
        var to = context.Date.AddDays(expiry.SoonDays);
        var documents = await db.Documents.AsNoTracking()
            .Where(d => d.ExpiresOn >= from && d.ExpiresOn <= to)
            .OrderBy(d => d.ExpiresOn)
            .ThenBy(d => d.Title)
            .ThenBy(d => d.Id)
            .Select(d => new { d.Id, d.Title, ExpiresOn = d.ExpiresOn!.Value })
            .ToListAsync(cancellationToken);

        var cards = documents.Select(d => Card(d.Id, d.Title, d.ExpiresOn, context.Date)).ToList();
        return new TodayModuleCards(cards);
    }

    public static TodayCard Card(Guid id, string title, DateOnly expiresOn, DateOnly today)
    {
        var days = expiresOn.DayNumber - today.DayNumber;
        var values = new Dictionary<string, TodayValue>
        {
            ["document"] = TodayValue.Plain(title),
            ["date"] = TodayValue.Day(expiresOn),
            ["days"] = TodayValue.Count(Math.Abs(days))
        };
        var link = $"/documents/{id}";
        return days < 0
            ? new TodayCard($"documents.expired.{id}", ModuleName, TodayCardType.Attention, "expired", "Document expired",
                null, "{document} expired on {date}", values, TodaySeverity.Bad, null, link)
            : new TodayCard($"documents.expiring.{id}", ModuleName, TodayCardType.Attention, "expiring", "Document expires soon",
                null, days == 0 ? "{document} expires today" : "{document} expires on {date}", values,
                days <= 7 ? TodaySeverity.Bad : TodaySeverity.Warning, null, link);
    }
}
