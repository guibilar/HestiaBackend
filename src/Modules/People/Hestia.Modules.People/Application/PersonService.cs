using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.People.Contracts;
using Hestia.Modules.People.Domain;
using Hestia.Modules.People.Infrastructure;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Platform.Exceptions;
using Hestia.Platform.Paging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hestia.Modules.People.Application;

/// <summary>The filters of the people list.</summary>
/// <param name="Group">One group (any letter case), or null.</param>
/// <param name="Text">Words to find in the name, the nickname, the relationship, the groups, the email, the phone, and the notes.</param>
/// <param name="Sort"><c>name</c> (default) or <c>contact</c> (the reach-out day, soonest first; people without a reminder last).</param>
/// <param name="Page">The page, from 1.</param>
/// <param name="PageSize">The page size, 1 to 100.</param>
public sealed record PersonQuery(string? Group, string? Text, string? Sort, int Page = 1, int PageSize = 20);

/// <summary>The people: add, list, change, delete, links, groups, and the summary of the People screen.</summary>
public sealed class PersonService(
    PeopleDbContext db,
    PersonLinkTargets linkTargets,
    IDocumentQueries documents,
    ITaskQueries tasks,
    PeopleReader reader,
    PeopleDays days,
    ILogger<PersonService> logger)
{
    public const int MaxSearchLength = 200;
    public const int MaxLinksPerRequest = 20;

    /// <summary>The latest contacts in <see cref="PersonDto.Interactions"/>.</summary>
    public const int RecentInteractions = 20;

    public async Task<PersonDto> CreateAsync(CreatePersonRequest request, CancellationToken cancellationToken)
    {
        var details = PersonDetails.Create(request.Name, request.Nickname, request.Relationship, request.Groups, request.Birthday,
            request.Email, request.Phone, request.Notes, request.ContactEveryDays, days.Today);
        var linkRequests = request.Links ?? [];
        if (linkRequests.Count > MaxLinksPerRequest)
        {
            throw new InvalidInputException("links", $"Add at most {MaxLinksPerRequest} links at once.");
        }
        // All links are checked before anything is saved.
        var targets = new List<(string Kind, string TargetId)>();
        foreach (var link in linkRequests)
        {
            var target = await linkTargets.CheckAsync(link?.Kind, link?.TargetId, cancellationToken);
            if (!targets.Contains(target))
            {
                targets.Add(target);
            }
        }

        var now = days.Now;
        var person = new Person { Id = Guid.CreateVersion7(), CreatedAt = now, UpdatedAt = now };
        details.ApplyTo(person);
        db.People.Add(person);
        foreach (var (kind, targetId) in targets)
        {
            db.Links.Add(new PersonLink { Id = Guid.CreateVersion7(), PersonId = person.Id, Kind = kind, TargetId = targetId, CreatedAt = now });
        }
        db.RaiseEvent(new PersonAdded(person.Id, person.Name, details.Groups, now));
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("People: added person {PersonId}", person.Id);
        return await GetAsync(person.Id, cancellationToken);
    }

    public async Task<PaginatedList<PersonSummaryDto>> ListAsync(PersonQuery query, CancellationToken cancellationToken)
    {
        PagingExtensions.Validate(query.Page, query.PageSize);
        var people = Filter(query);
        var sort = query.Sort?.Trim().ToLowerInvariant();
        var today = days.Today;
        switch (sort)
        {
            case null or "" or "name":
            {
                var total = await people.CountAsync(cancellationToken);
                var page = await people.OrderBy(p => p.Name).ThenBy(p => p.Id)
                    .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(cancellationToken);
                return Page(page.Select(p => reader.Summary(p, today)).ToList(), query, total);
            }
            case "contact":
            {
                // The reach-out day is computed (last contact or add day, plus the reminder): sort in memory. A home has
                // some hundred people.
                var all = (await people.ToListAsync(cancellationToken)).Select(p => reader.Summary(p, today))
                    .OrderBy(p => p.ContactDueOn is null)
                    .ThenBy(p => p.ContactDueOn)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p.Id)
                    .ToList();
                return Page(all.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList(), query, all.Count);
            }
            default:
                throw new InvalidInputException("sort", "Use sort=name or sort=contact.");
        }
    }

    private static PaginatedList<PersonSummaryDto> Page(List<PersonSummaryDto> items, PersonQuery query, int total) =>
        new() { Items = items, Page = query.Page, PageSize = query.PageSize, TotalCount = total };

    /// <exception cref="InvalidInputException">A filter cannot be used.</exception>
    private IQueryable<Person> Filter(PersonQuery query)
    {
        IQueryable<Person> people = db.People.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Group))
        {
            var groups = PersonGroups.Normalize([query.Group]);
            if (groups.Count != 1)
            {
                throw new InvalidInputException("group", "Give one group.");
            }
            var pattern = PersonGroups.Pattern(groups[0]);
            people = people.Where(p => p.Groups.Contains(pattern));
        }

        var text = query.Text?.Trim() ?? string.Empty;
        if (text.Length > MaxSearchLength)
        {
            throw new InvalidInputException("q", $"The search text can have at most {MaxSearchLength} characters.");
        }
        // Each word must be in one of the texts (any letter case). A list of people is short: no full-text index.
        foreach (var word in text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            people = people.Where(p =>
                p.Name.ToLower().Contains(word) || p.Groups.Contains(word) ||
                (p.Nickname != null && p.Nickname.ToLower().Contains(word)) ||
                (p.Relationship != null && p.Relationship.ToLower().Contains(word)) ||
                (p.Email != null && p.Email.ToLower().Contains(word)) ||
                (p.Phone != null && p.Phone.Contains(word)) ||
                (p.Notes != null && p.Notes.ToLower().Contains(word)));
        }
        return people;
    }

    public async Task<PersonDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var person = await db.People.AsNoTracking().Include(p => p.Links).FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException("Person", id);
        var interactions = await db.Interactions.AsNoTracking().Where(i => i.PersonId == id)
            .OrderByDescending(i => i.OccurredOn).ThenByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
            .Take(RecentInteractions).ToListAsync(cancellationToken);
        var interactionCount = await db.Interactions.CountAsync(i => i.PersonId == id, cancellationToken);
        var gifts = (await db.Gifts.AsNoTracking().Where(g => g.PersonId == id).ToListAsync(cancellationToken))
            .OrderBy(g => g.GivenOn is not null)
            .ThenBy(g => g.GivenOn is null ? g.CreatedAt.UtcTicks : -g.GivenOn.Value.DayNumber)
            .ThenBy(g => g.Id)
            .ToList();
        var loans = (await db.Loans.AsNoTracking().Where(l => l.PersonId == id).ToListAsync(cancellationToken))
            .OrderBy(l => l.ReturnedOn is not null)
            .ThenBy(l => l.ReturnedOn is null ? l.LentOn.DayNumber : -l.ReturnedOn.Value.DayNumber)
            .ThenBy(l => l.Id)
            .ToList();
        var today = days.Today;
        return new PersonDto(
            reader.Summary(person, today),
            person.Notes,
            interactions.Select(PeopleReader.Interaction).ToList(),
            interactionCount,
            gifts.Select(PeopleReader.Gift).ToList(),
            loans.Select(l => PeopleReader.Loan(l, person.Name, today)).ToList(),
            person.Links.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id).Select(PeopleReader.Link).ToList(),
            await LinkedFromAsync(id, cancellationToken));
    }

    /// <summary>The documents and the tasks that link to a person (kind <c>People.Person</c> there, T93).</summary>
    private async Task<List<PersonLinkedFromDto>> LinkedFromAsync(Guid id, CancellationToken cancellationToken)
    {
        var target = id.ToString("D");
        var linkedDocuments = await documents.ListByLinkAsync(DocumentLinkKinds.PeoplePerson, target, cancellationToken);
        var linkedTasks = await tasks.ListByLinkAsync(TaskLinkKinds.PeoplePerson, target, cancellationToken);
        return linkedDocuments
            .OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Id)
            .Select(d => new PersonLinkedFromDto(PersonLinkKinds.DocumentsDocument, d.Id.ToString("D"), d.Title))
            .Concat(linkedTasks
                .OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Id)
                .Select(t => new PersonLinkedFromDto(PersonLinkKinds.TasksTask, t.Id.ToString("D"), t.Title)))
            .ToList();
    }

    public async Task<PersonDto> UpdateAsync(Guid id, UpdatePersonRequest request, CancellationToken cancellationToken)
    {
        var details = PersonDetails.Create(request.Name, request.Nickname, request.Relationship, request.Groups, request.Birthday,
            request.Email, request.Phone, request.Notes, request.ContactEveryDays, days.Today);
        var person = await db.PersonAsync(id, cancellationToken);
        var now = days.Now;
        details.ApplyTo(person);
        person.UpdatedAt = now;
        db.RaiseEvent(new PersonChanged(person.Id, PersonChange.Details, person.Name, details.Groups, now));
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var person = await db.PersonAsync(id, cancellationToken);
        db.RaiseEvent(new PersonDeleted(person.Id, person.Name, days.Now));
        // The records and the links go with the person. The foreign keys cascade in PostgreSQL; removing them here also
        // works for the in-memory test database, which cascades only tracked rows.
        db.Interactions.RemoveRange(await db.Interactions.Where(i => i.PersonId == id).ToListAsync(cancellationToken));
        db.Gifts.RemoveRange(await db.Gifts.Where(g => g.PersonId == id).ToListAsync(cancellationToken));
        db.Loans.RemoveRange(await db.Loans.Where(l => l.PersonId == id).ToListAsync(cancellationToken));
        db.Links.RemoveRange(await db.Links.Where(l => l.PersonId == id).ToListAsync(cancellationToken));
        db.People.Remove(person);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("People: deleted person {PersonId}", id);
    }

    public async Task<PersonLinkDto> AddLinkAsync(Guid id, CreatePersonLinkRequest request, CancellationToken cancellationToken)
    {
        var person = await db.PersonAsync(id, cancellationToken);
        var (kind, targetId) = await linkTargets.CheckAsync(request.Kind, request.TargetId, cancellationToken);
        if (await db.Links.AnyAsync(l => l.PersonId == id && l.Kind == kind && l.TargetId == targetId, cancellationToken))
        {
            throw new ConflictException("The person has this link already.");
        }
        var now = days.Now;
        var link = new PersonLink { Id = Guid.CreateVersion7(), PersonId = id, Kind = kind, TargetId = targetId, CreatedAt = now };
        db.Links.Add(link);
        person.UpdatedAt = now;
        db.RaiseEvent(Changed(person, PersonChange.Links, now));
        await db.SaveChangesAsync(cancellationToken);
        return PeopleReader.Link(link);
    }

    public async Task RemoveLinkAsync(Guid id, Guid linkId, CancellationToken cancellationToken)
    {
        var link = await db.Links.FirstOrDefaultAsync(l => l.Id == linkId && l.PersonId == id, cancellationToken)
            ?? throw new NotFoundException("Link", linkId);
        var person = await db.PersonAsync(id, cancellationToken);
        var now = days.Now;
        db.Links.Remove(link);
        person.UpdatedAt = now;
        db.RaiseEvent(Changed(person, PersonChange.Links, now));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The people of one thing, by name, from both sides of the link: the people that link to it, and (for a document or a
    /// task) the people that it links to (kind <c>People.Person</c>, T93).
    /// </summary>
    public async Task<List<PersonSummaryDto>> ListByLinkAsync(string? kind, string? targetId, CancellationToken cancellationToken)
    {
        var (linkKind, target) = PersonLinkTargets.Normalize(kind, targetId);
        var linkedTo = await PeopleLinkedFromAsync(linkKind, Guid.Parse(target), cancellationToken);
        var people = await db.People.AsNoTracking()
            .Where(p => p.Links.Any(l => l.Kind == linkKind && l.TargetId == target) || linkedTo.Contains(p.Id))
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .ToListAsync(cancellationToken);
        var today = days.Today;
        return people.Select(p => reader.Summary(p, today)).ToList();
    }

    /// <summary>The ids of the people that a document or a task links to (kind <c>People.Person</c>). Other kinds give none.</summary>
    private async Task<List<Guid>> PeopleLinkedFromAsync(string kind, Guid targetId, CancellationToken cancellationToken)
    {
        IEnumerable<string> targets = kind switch
        {
            PersonLinkKinds.DocumentsDocument => (await documents.ListAsync([targetId], cancellationToken))
                .SelectMany(d => d.Links).Where(l => l.Kind == DocumentLinkKinds.PeoplePerson).Select(l => l.TargetId),
            PersonLinkKinds.TasksTask => (await tasks.FindAsync(targetId, cancellationToken))?.Links
                .Where(l => l.Kind == TaskLinkKinds.PeoplePerson).Select(l => l.TargetId) ?? [],
            _ => []
        };
        return targets.Select(t => Guid.TryParse(t, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToList();
    }

    /// <summary>The groups in use, by name, with the count of people in each.</summary>
    public async Task<List<GroupCountDto>> GroupsAsync(CancellationToken cancellationToken)
    {
        var stored = await db.People.AsNoTracking().Where(p => p.Groups != string.Empty).Select(p => p.Groups)
            .ToListAsync(cancellationToken);
        return stored.SelectMany(PersonGroups.Read)
            .GroupBy(g => g)
            .Select(g => new GroupCountDto(g.Key, g.Count()))
            .OrderBy(g => g.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The numbers of the People screen: reach out, upcoming birthdays, and loans.</summary>
    public async Task<PeopleSummaryDto> SummaryAsync(CancellationToken cancellationToken)
    {
        var today = days.Today;
        var people = await db.People.AsNoTracking().ToListAsync(cancellationToken);
        var summaries = people.Select(p => reader.Summary(p, today)).ToList();
        var reachOut = summaries.Where(p => p.ContactOverdue)
            .OrderBy(p => p.ContactDueOn).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)
            .ToList();

        var until = today.AddDays(days.Options.UpcomingDays);
        var openIdeas = await db.Gifts.AsNoTracking().Where(g => g.GivenOn == null)
            .GroupBy(g => g.PersonId).Select(g => new { PersonId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.PersonId, g => g.Count, cancellationToken);
        var upcoming = people
            .Select(p => (Person: p, Next: Birthdays.Next(p, today)))
            .Where(p => p.Next is { } next && next <= until)
            .Select(p => new UpcomingBirthdayDto(p.Person.Id, p.Person.Name, p.Next!.Value, p.Next.Value.DayNumber - today.DayNumber,
                Birthdays.AgeOn(p.Person, p.Next.Value), openIdeas.GetValueOrDefault(p.Person.Id)))
            .OrderBy(b => b.Date).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.PersonId)
            .ToList();

        var openLoans = await db.Loans.AsNoTracking().Where(l => l.ReturnedOn == null).Select(l => l.DueOn).ToListAsync(cancellationToken);
        return new PeopleSummaryDto(today, people.Count, reachOut, upcoming, openLoans.Count, openLoans.Count(due => due < today));
    }

    private static PersonChanged Changed(Person person, PersonChange change, DateTimeOffset now) =>
        new(person.Id, change, person.Name, PersonGroups.Read(person.Groups), now);
}

/// <summary>The details of a person that the user sets: checked and cleaned for a create and for a change.</summary>
public sealed record PersonDetails(
    string Name,
    string? Nickname,
    string? Relationship,
    List<string> Groups,
    BirthdayDto? Birthday,
    string? Email,
    string? Phone,
    string? Notes,
    int? ContactEveryDays)
{
    /// <exception cref="InvalidInputException">A value cannot be used.</exception>
    public static PersonDetails Create(string? name, string? nickname, string? relationship, IEnumerable<string?>? groups,
        BirthdayDto? birthday, string? email, string? phone, string? notes, int? contactEveryDays, DateOnly today)
    {
        if (contactEveryDays is < 1 or > Person.MaxContactEveryDays)
        {
            throw new InvalidInputException("contactEveryDays", $"The reminder must be from 1 to {Person.MaxContactEveryDays} days.");
        }
        var cleanEmail = PeopleText.Line(email, "email", Person.MaxEmailLength);
        if (cleanEmail is not null && (!cleanEmail.Contains('@') || cleanEmail.Contains(' ')))
        {
            throw new InvalidInputException("email", "Enter an email address, for example name@example.com.");
        }
        return new PersonDetails(
            PeopleText.Required(name, "name", Person.MaxNameLength, "Enter a name."),
            PeopleText.Line(nickname, "nickname", Person.MaxNicknameLength),
            PeopleText.Line(relationship, "relationship", Person.MaxRelationshipLength),
            PersonGroups.Normalize(groups),
            Birthdays.Check(birthday, today),
            cleanEmail,
            PeopleText.Line(phone, "phone", Person.MaxPhoneLength),
            PeopleText.Block(notes, "notes", Person.MaxNotesLength),
            contactEveryDays);
    }

    public void ApplyTo(Person person)
    {
        person.Name = Name;
        person.Nickname = Nickname;
        person.Relationship = Relationship;
        person.Groups = PersonGroups.Store(Groups);
        person.BirthMonth = Birthday?.Month;
        person.BirthDay = Birthday?.Day;
        person.BirthYear = Birthday?.Year;
        person.Email = Email;
        person.Phone = Phone;
        person.Notes = Notes;
        person.ContactEveryDays = ContactEveryDays;
    }
}

/// <summary>The other modules' queries (<see cref="IPeopleQueries"/>). They read only the People tables.</summary>
public sealed class PeopleQueries(PeopleDbContext db, PeopleReader reader) : IPeopleQueries
{
    public async Task<PersonSummaryDto?> FindPersonAsync(Guid id, CancellationToken cancellationToken = default) =>
        await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken) is { } person
            ? reader.Summary(person)
            : null;

    public async Task<IReadOnlyList<PersonSummaryDto>> ListByLinkAsync(string kind, string targetId, CancellationToken cancellationToken = default)
    {
        string linkKind, target;
        try
        {
            (linkKind, target) = PersonLinkTargets.Normalize(kind, targetId);
        }
        catch (InvalidInputException)
        {
            return [];
        }
        var people = await db.People.AsNoTracking()
            .Where(p => p.Links.Any(l => l.Kind == linkKind && l.TargetId == target))
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .ToListAsync(cancellationToken);
        return people.Select(reader.Summary).ToList();
    }
}
