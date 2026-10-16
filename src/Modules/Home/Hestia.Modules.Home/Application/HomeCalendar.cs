using Hestia.Modules.Calendar.Contracts;
using Hestia.Modules.Home.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.Home.Application;

/// <summary>The Calendar items of the module (T103): each home item on the last day of its warranty.</summary>
public sealed class HomeCalendar(HomeDbContext db) : ICalendarSource
{
    public string Module => "Home";

    public async Task<CalendarItems> ListAsync(CalendarRange range, CancellationToken cancellationToken)
    {
        var items = await db.Items.AsNoTracking()
            .Where(i => i.WarrantyUntil >= range.From && i.WarrantyUntil <= range.To)
            .Select(i => new { i.Id, i.Name, i.WarrantyUntil })
            .ToListAsync(cancellationToken);
        return new CalendarItems(items.Select(i => CalendarItem.Day($"home.warranty-ends.{i.Id:D}", Module, "warranty-ends",
            $"Warranty of {i.Name} ends", null, i.WarrantyUntil!.Value, $"/home/items/{i.Id:D}")).ToList());
    }
}
