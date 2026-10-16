using Hestia.Modules.People.Contracts;
using Hestia.Modules.People.Domain;
using Hestia.Modules.People.Infrastructure;
using Hestia.Platform.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hestia.Modules.People.Application;

/// <summary>
/// The import of contacts (T105): from a vCard file, or from a list (the phone contacts on Android). A contact that matches
/// a person (the same email, then the same phone, then the same name) only fills the empty details of that person and adds
/// its groups: the import never overwrites what the user wrote. A dry run gives the preview.
/// </summary>
public sealed class PeopleImport(PeopleDbContext db, PeopleDays days, ILogger<PeopleImport> logger)
{
    public async Task<PeopleImportResultDto> ImportAsync(PeopleImportRequest request, CancellationToken cancellationToken)
    {
        var contacts = Contacts(request);
        var only = request.Only?.ToHashSet();
        var today = days.Today;
        var now = days.Now;

        var people = await db.People.ToListAsync(cancellationToken);
        var matcher = new Matcher(people);
        var items = new List<PeopleImportItemDto>();
        var added = new List<Person>();
        var changed = new List<Person>();

        for (var index = 0; index < contacts.Count; index++)
        {
            var contact = contacts[index];
            PeopleImportItemDto Item(PeopleImportAction action, Person? match = null, string? by = null, IReadOnlyList<string>? fields = null,
                string? reason = null) =>
                new(index, action, contact, match?.Id, match?.Name, by, fields ?? [], reason);

            PersonDetails details;
            try
            {
                details = PersonDetails.Create(contact.Name, contact.Nickname, contact.Relationship, contact.Groups, contact.Birthday,
                    contact.Email, contact.Phone, contact.Notes, contact.ContactEveryDays, today);
            }
            catch (InvalidInputException exception)
            {
                items.Add(Item(PeopleImportAction.Skip, reason: exception.Message));
                continue;
            }

            var (match, by) = matcher.Find(details);
            if (only is not null && !only.Contains(index))
            {
                items.Add(Item(PeopleImportAction.Skip, match, by, reason: "Not chosen."));
                continue;
            }
            if (match is null)
            {
                var person = new Person { Id = Guid.CreateVersion7(), CreatedAt = now, UpdatedAt = now };
                details.ApplyTo(person);
                // A later contact of the same file can match this one.
                matcher.Add(person);
                added.Add(person);
                items.Add(Item(PeopleImportAction.Add, person));
                continue;
            }

            var fields = Merge(match, details, apply: !request.DryRun);
            if (fields.Count == 0)
            {
                items.Add(Item(PeopleImportAction.Same, match, by));
                continue;
            }
            if (!request.DryRun)
            {
                match.UpdatedAt = now;
                matcher.Add(match);
                if (!added.Contains(match) && !changed.Contains(match))
                {
                    changed.Add(match);
                }
            }
            items.Add(Item(PeopleImportAction.Merge, match, by, fields));
        }

        if (!request.DryRun)
        {
            foreach (var person in added)
            {
                db.People.Add(person);
                db.RaiseEvent(new PersonAdded(person.Id, person.Name, PersonGroups.Read(person.Groups), now));
            }
            foreach (var person in changed)
            {
                db.RaiseEvent(new PersonChanged(person.Id, PersonChange.Details, person.Name, PersonGroups.Read(person.Groups), now));
            }
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("People: imported {Added} new and {Merged} merged people", added.Count, changed.Count);
        }
        else
        {
            // A dry run changes nothing: the new people were never added to the DbContext, so they have no id. A later
            // contact of the file that matches one of them keeps the name of the match.
            var newIds = added.Select(p => p.Id).ToHashSet();
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].ExistingId is { } id && newIds.Contains(id))
                {
                    items[i] = items[i].Action == PeopleImportAction.Add
                        ? items[i] with { ExistingId = null, ExistingName = null }
                        : items[i] with { ExistingId = null };
                }
            }
        }

        return new PeopleImportResultDto(request.DryRun,
            items.Count(i => i.Action == PeopleImportAction.Add),
            items.Count(i => i.Action == PeopleImportAction.Merge),
            items.Count(i => i.Action == PeopleImportAction.Same),
            items.Count(i => i.Action == PeopleImportAction.Skip),
            items);
    }

    private static List<CreatePersonRequest> Contacts(PeopleImportRequest request)
    {
        var hasVCard = !string.IsNullOrWhiteSpace(request.VCard);
        var hasPeople = request.People is { Count: > 0 };
        if (hasVCard == hasPeople)
        {
            throw new InvalidInputException("vCard", "Send the text of a vCard file (vCard) or a list of people (people), not both.");
        }
        List<CreatePersonRequest> contacts;
        if (hasVCard)
        {
            if (request.VCard!.Length > PeopleImportLimits.MaxVCardLength)
            {
                throw new InvalidInputException("vCard", $"The file is too big (at most {PeopleImportLimits.MaxVCardLength / 1_000_000} MB).");
            }
            if (!request.VCard.Contains("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidInputException("vCard", "This is not a vCard (.vcf) file: it has no BEGIN:VCARD.");
            }
            contacts = VCardReader.Read(request.VCard).Select(c => c.Person).ToList();
        }
        else
        {
            // The links of a person are not part of an import.
            contacts = request.People!.Select(p => (p ?? new CreatePersonRequest(null)) with { Links = null }).ToList();
        }
        if (contacts.Count > PeopleImportLimits.MaxContacts)
        {
            throw new InvalidInputException("people", $"An import can have at most {PeopleImportLimits.MaxContacts} contacts; this one has {contacts.Count}.");
        }
        return contacts;
    }

    /// <summary>Fills the empty details of <paramref name="person"/> and adds the groups. Returns the names of the details that change.</summary>
    private static List<string> Merge(Person person, PersonDetails details, bool apply)
    {
        var fields = new List<string>();
        void Fill(string field, bool empty, bool has, Action set)
        {
            if (empty && has)
            {
                fields.Add(field);
                if (apply)
                {
                    set();
                }
            }
        }

        Fill("nickname", person.Nickname is null, details.Nickname is not null, () => person.Nickname = details.Nickname);
        Fill("relationship", person.Relationship is null, details.Relationship is not null, () => person.Relationship = details.Relationship);
        Fill("email", person.Email is null, details.Email is not null, () => person.Email = details.Email);
        Fill("phone", person.Phone is null, details.Phone is not null, () => person.Phone = details.Phone);
        Fill("notes", person.Notes is null, details.Notes is not null, () => person.Notes = details.Notes);
        Fill("birthday", person.BirthMonth is null, details.Birthday is not null, () =>
        {
            person.BirthMonth = details.Birthday!.Month;
            person.BirthDay = details.Birthday.Day;
            person.BirthYear = details.Birthday.Year;
        });
        var groups = PersonGroups.Read(person.Groups);
        var newGroups = details.Groups.Where(g => !groups.Contains(g)).ToList();
        if (newGroups.Count > 0 && groups.Count + newGroups.Count <= PersonGroups.MaxGroups)
        {
            fields.Add("groups");
            if (apply)
            {
                person.Groups = PersonGroups.Store(groups.Concat(newGroups).ToList());
            }
        }
        return fields;
    }

    /// <summary>Finds the person of a contact: the same email, then the same phone (its last 10 digits), then the same name.</summary>
    private sealed class Matcher
    {
        private readonly Dictionary<string, Person> _emails = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Person> _phones = new();
        private readonly Dictionary<string, Person> _names = new(StringComparer.CurrentCultureIgnoreCase);

        public Matcher(IEnumerable<Person> people)
        {
            foreach (var person in people)
            {
                Add(person);
            }
        }

        public void Add(Person person)
        {
            if (person.Email is { } email)
            {
                _emails.TryAdd(email, person);
            }
            if (Digits(person.Phone) is { } phone)
            {
                _phones.TryAdd(phone, person);
            }
            _names.TryAdd(PersonGroups.OneLine(person.Name), person);
        }

        public (Person? Person, string? By) Find(PersonDetails details)
        {
            if (details.Email is { } email && _emails.TryGetValue(email, out var byEmail))
            {
                return (byEmail, "email");
            }
            if (Digits(details.Phone) is { } phone && _phones.TryGetValue(phone, out var byPhone))
            {
                return (byPhone, "phone");
            }
            return _names.TryGetValue(details.Name, out var byName) ? (byName, "name") : (null, null);
        }

        /// <summary>The last 10 digits of a phone number (so +1 and the local form match), or null for fewer than 7.</summary>
        private static string? Digits(string? phone)
        {
            var digits = new string((phone ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
            return digits.Length < 7 ? null : digits.Length > 10 ? digits[^10..] : digits;
        }
    }
}
