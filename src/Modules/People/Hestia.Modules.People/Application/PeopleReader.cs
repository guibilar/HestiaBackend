using Hestia.Modules.People.Contracts;
using Hestia.Modules.People.Domain;
using Hestia.Modules.People.Infrastructure;
using Hestia.Platform.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.People.Application;

/// <summary>Makes the DTOs of the module. The dates that depend on today (next birthday, reach-out day) use <see cref="PeopleDays"/>.</summary>
public sealed class PeopleReader(PeopleDays days)
{
    public PersonSummaryDto Summary(Person person) => Summary(person, days.Today);

    public PersonSummaryDto Summary(Person person, DateOnly today)
    {
        var due = ContactDueOn(person);
        return new PersonSummaryDto(person.Id, person.Name, person.Nickname, person.Relationship, PersonGroups.Read(person.Groups),
            person is { BirthMonth: { } month, BirthDay: { } day } ? new BirthdayDto(month, day, person.BirthYear) : null,
            Birthdays.Next(person, today), person.Email, person.Phone, person.ContactEveryDays, person.LastContactOn, due,
            due < today, person.CreatedAt, person.UpdatedAt);
    }

    /// <summary>
    /// The day to reach out: the last contact (or, without one, the day the person was added) plus the reminder. Null
    /// without a reminder.
    /// </summary>
    public DateOnly? ContactDueOn(Person person) =>
        person.ContactEveryDays is { } every ? (person.LastContactOn ?? days.DayOf(person.CreatedAt)).AddDays(every) : null;

    public static InteractionDto Interaction(Interaction interaction) =>
        new(interaction.Id, interaction.PersonId, interaction.OccurredOn, interaction.Kind, interaction.Note, interaction.CreatedAt);

    public static GiftIdeaDto Gift(GiftIdea gift) =>
        new(gift.Id, gift.PersonId, gift.Title, gift.Notes, gift.Price, gift.Occasion, gift.GivenOn, gift.CreatedAt);

    public LoanDto Loan(Loan loan, string personName) => Loan(loan, personName, days.Today);

    public static LoanDto Loan(Loan loan, string personName, DateOnly today) =>
        new(loan.Id, loan.PersonId, personName, loan.ItemName, loan.Direction, loan.LentOn, loan.DueOn, loan.ReturnedOn,
            loan.ReturnedOn is null && loan.DueOn < today, loan.Notes, loan.CreatedAt);

    public static PersonLinkDto Link(PersonLink link) => new(link.Id, link.Kind, link.TargetId, link.CreatedAt);
}

/// <summary>The queries that the services share.</summary>
internal static class PeopleData
{
    /// <exception cref="NotFoundException">No person has this id.</exception>
    public static async Task<Person> PersonAsync(this PeopleDbContext db, Guid id, CancellationToken cancellationToken) =>
        await db.People.FirstOrDefaultAsync(p => p.Id == id, cancellationToken) ?? throw new NotFoundException("Person", id);

    /// <summary>Sets <see cref="Person.LastContactOn"/> to the latest contact, after the changes that the context tracks.</summary>
    public static async Task RefreshLastContactAsync(this PeopleDbContext db, Person person, CancellationToken cancellationToken)
    {
        // The tracked changes (an add, a new day, a delete) are not in the database yet: combine both.
        var tracked = db.ChangeTracker.Entries<Interaction>().Where(e => e.Entity.PersonId == person.Id).ToList();
        var trackedIds = tracked.Select(e => e.Entity.Id).ToList();
        var stored = await db.Interactions.AsNoTracking()
            .Where(i => i.PersonId == person.Id && !trackedIds.Contains(i.Id))
            .MaxAsync(i => (DateOnly?)i.OccurredOn, cancellationToken);
        var latestTracked = tracked.Where(e => e.State != EntityState.Deleted && e.State != EntityState.Detached)
            .Select(e => (DateOnly?)e.Entity.OccurredOn).Max();
        person.LastContactOn = Max(stored, latestTracked);
    }

    private static DateOnly? Max(DateOnly? a, DateOnly? b) => a is null ? b : b is null ? a : a > b ? a : b;
}
