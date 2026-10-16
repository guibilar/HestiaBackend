using Hestia.Modules.Calendar.Contracts;
using Hestia.Modules.People.Contracts;
using Hestia.Modules.People.Domain;
using Hestia.Modules.People.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.People.Application;

/// <summary>
/// The Calendar items of the module (T103): each birthday in the range (every year), the reach-out day of a reminder, and
/// the due day of an open loan.
/// </summary>
public sealed class PeopleCalendar(PeopleDbContext db, PeopleReader reader) : ICalendarSource
{
    public string Module => "People";

    public async Task<CalendarItems> ListAsync(CalendarRange range, CancellationToken cancellationToken)
    {
        var items = new List<CalendarItem>();
        var people = await db.People.AsNoTracking()
            .Where(p => p.BirthMonth != null || p.ContactEveryDays != null)
            .ToListAsync(cancellationToken);
        foreach (var person in people)
        {
            if (person is { BirthMonth: { } month, BirthDay: { } day })
            {
                for (var year = range.From.Year; year <= range.To.Year; year++)
                {
                    var date = Birthdays.InYear(month, day, year);
                    if (range.Contains(date))
                    {
                        var age = Birthdays.AgeOn(person, date);
                        items.Add(CalendarItem.Day($"people.birthday.{person.Id:D}.{year}", Module, "birthday",
                            $"{person.Name}'s birthday", age is { } a and > 0 ? $"Turns {a}" : null, date, $"/people/{person.Id:D}"));
                    }
                }
            }
            if (reader.ContactDueOn(person) is { } due && range.Contains(due))
            {
                items.Add(CalendarItem.Day($"people.reach-out.{person.Id:D}", Module, "reach-out", $"Reach out to {person.Name}",
                    person.Relationship, due, $"/people/{person.Id:D}"));
            }
        }

        var loans = await db.Loans.AsNoTracking()
            .Where(l => l.ReturnedOn == null && l.DueOn >= range.From && l.DueOn <= range.To)
            .Join(db.People, l => l.PersonId, p => p.Id, (l, p) => new { l.Id, l.ItemName, l.Direction, l.DueOn, p.Name, PersonId = p.Id })
            .ToListAsync(cancellationToken);
        items.AddRange(loans.Select(l => CalendarItem.Day($"people.loan-due.{l.Id:D}", Module, "loan-due",
            l.Direction == LoanDirection.Lent ? $"{l.ItemName} back from {l.Name}" : $"Give {l.ItemName} back to {l.Name}",
            null, l.DueOn!.Value, $"/people/{l.PersonId:D}")));
        return new CalendarItems(items);
    }
}
