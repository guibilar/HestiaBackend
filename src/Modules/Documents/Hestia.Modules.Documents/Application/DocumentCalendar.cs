using Hestia.Modules.Calendar.Contracts;
using Hestia.Modules.Documents.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.Documents.Application;

/// <summary>The Calendar items of the module (T103): each document on the day it expires.</summary>
public sealed class DocumentCalendar(DocumentsDbContext db) : ICalendarSource
{
    public string Module => "Documents";

    public async Task<CalendarItems> ListAsync(CalendarRange range, CancellationToken cancellationToken)
    {
        var documents = await db.Documents.AsNoTracking()
            .Where(d => d.ExpiresOn >= range.From && d.ExpiresOn <= range.To)
            .Select(d => new { d.Id, d.Title, d.Kind, d.ExpiresOn })
            .ToListAsync(cancellationToken);
        return new CalendarItems(documents.Select(d => CalendarItem.Day($"documents.expires.{d.Id:D}", Module, "document-expires",
            $"{d.Title} expires", d.Kind.ToString(), d.ExpiresOn!.Value, $"/documents/{d.Id:D}")).ToList());
    }
}
