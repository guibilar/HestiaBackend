using Hestia.Modules.People.Contracts;
using Hestia.Modules.People.Infrastructure;
using Hestia.Modules.Timeline.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.People.Application;

/// <summary>
/// The People entries of the Timeline (T16): the contacts (on their day), the things lent or borrowed, the gifts given,
/// and the people added.
/// </summary>
public sealed class PeopleTimeline(PeopleDbContext db) : ITimelineSource
{
    public string Module => "People";

    public async Task<TimelineEntries> ListAsync(TimelineRange range, CancellationToken cancellationToken)
    {
        var (from, to, start, end) = (range.From, range.To, range.Start, range.End);
        var contacts = await db.Interactions.AsNoTracking().Where(i => i.OccurredOn >= from && i.OccurredOn <= to).ToListAsync(cancellationToken);
        var loans = await db.Loans.AsNoTracking().Where(l => l.LentOn >= from && l.LentOn <= to).ToListAsync(cancellationToken);
        var gifts = await db.Gifts.AsNoTracking().Where(g => g.GivenOn >= from && g.GivenOn <= to).ToListAsync(cancellationToken);
        var added = await db.People.AsNoTracking().Where(p => p.CreatedAt >= start && p.CreatedAt < end)
            .Select(p => new { p.Id, p.Name, p.Relationship, p.CreatedAt }).ToListAsync(cancellationToken);

        var ids = contacts.Select(c => c.PersonId).Concat(loans.Select(l => l.PersonId)).Concat(gifts.Select(g => g.PersonId)).Distinct().ToList();
        var names = await db.People.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        string Name(Guid id) => names.GetValueOrDefault(id, "someone");

        var entries = new List<TimelineEntry>();
        entries.AddRange(contacts.Select(c => new TimelineEntry(
            $"people.contact.{c.Id:D}", Module, "contact", c.OccurredOn, null,
            string.IsNullOrWhiteSpace(c.Kind) ? $"Contact with {Name(c.PersonId)}" : $"{c.Kind} with {Name(c.PersonId)}",
            c.Note, null, $"/people/{c.PersonId:D}")));
        entries.AddRange(loans.Select(l => new TimelineEntry(
            $"people.loan.{l.Id:D}", Module, "loan", l.LentOn, null,
            l.Direction == LoanDirection.Lent ? $"Lent {l.ItemName} to {Name(l.PersonId)}" : $"Borrowed {l.ItemName} from {Name(l.PersonId)}",
            l.ReturnedOn is null ? "Not back yet" : "Back", null, $"/people/{l.PersonId:D}")));
        entries.AddRange(gifts.Select(g => new TimelineEntry(
            $"people.gift-given.{g.Id:D}", Module, "gift-given", g.GivenOn!.Value, null,
            $"Gave {g.Title} to {Name(g.PersonId)}", g.Occasion, null, $"/people/{g.PersonId:D}")));
        entries.AddRange(added.Select(p => new TimelineEntry(
            $"people.person-added.{p.Id:D}", Module, "person-added", range.DayOf(p.CreatedAt), p.CreatedAt,
            $"Added {p.Name}", p.Relationship, null, $"/people/{p.Id:D}")));
        return new TimelineEntries(entries);
    }
}
