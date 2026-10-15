using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.People.Contracts;
using Hestia.Modules.People.Domain;
using Hestia.Modules.People.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.People.Application;

/// <summary>
/// The People alert rules (T20), all Neutral, each opening the person (<c>/people/&lt;id&gt;</c>):
/// <list type="bullet">
/// <item><c>people.reach-out.&lt;id&gt;</c>: the reach-out reminder is overdue (see <see cref="PeopleReader.ContactDueOn"/>);</item>
/// <item><c>people.birthday.&lt;id&gt;</c>: a birthday today or in the next 7 days (<c>People:BirthdayAlertDays</c>);</item>
/// <item><c>people.loan-overdue.&lt;loan id&gt;</c>: an open loan after its due day.</item>
/// </list>
/// </summary>
/// <remarks>
/// A list of people is short, so each check looks at all people and covers all prefixes, also for a check of one target
/// (a loan alert has the loan id, and a person event cannot tell which loans changed). "Today" is the day of the check in
/// <c>People:TimeZone</c>.
/// </remarks>
public sealed class ReminderAlerts(PeopleDbContext db, PeopleDays days) : IAlertSource
{
    public const string SourceName = "People.Reminders";
    public const string ModuleName = "People";
    public const string ReachOutPrefix = "people.reach-out.";
    public const string BirthdayPrefix = "people.birthday.";
    public const string LoanPrefix = "people.loan-overdue.";

    public string Name => SourceName;

    public string Module => ModuleName;

    /// <summary>After Documents (100), Tasks (110), and Home (120).</summary>
    public int Order => 130;

    public async Task<AlertCheckResult> CheckAsync(AlertCheckContext context, CancellationToken cancellationToken)
    {
        var today = days.DayOf(context.Now);
        var people = await db.People.AsNoTracking().ToListAsync(cancellationToken);
        var alerts = new List<AlertCandidate>();
        var birthdayUntil = today.AddDays(days.Options.BirthdayAlertDays);
        foreach (var person in people)
        {
            if (person.ContactEveryDays is { } every)
            {
                var last = person.LastContactOn ?? days.DayOf(person.CreatedAt);
                if (last.AddDays(every) < today)
                {
                    alerts.Add(ReachOut(person, last, today));
                }
            }
            if (Birthdays.Next(person, today) is { } next && next <= birthdayUntil)
            {
                alerts.Add(Birthday(person, next, today));
            }
        }

        var names = people.ToDictionary(p => p.Id, p => p.Name);
        var overdue = await db.Loans.AsNoTracking().Where(l => l.ReturnedOn == null && l.DueOn < today).ToListAsync(cancellationToken);
        alerts.AddRange(overdue.Where(l => names.ContainsKey(l.PersonId)).Select(l => LoanOverdue(l, names[l.PersonId], today)));
        return new AlertCheckResult(alerts, [ReachOutPrefix, BirthdayPrefix, LoanPrefix]);
    }

    /// <summary>The reach-out alert of a person whose last contact (or add day) is <paramref name="last"/>.</summary>
    public static AlertCandidate ReachOut(Person person, DateOnly last, DateOnly today)
    {
        var since = today.DayNumber - last.DayNumber;
        var values = new Dictionary<string, AlertValue>
        {
            ["person"] = AlertValue.Plain(person.Name),
            ["date"] = AlertValue.Day(last),
            ["days"] = AlertValue.Count(since)
        };
        var text = person.LastContactOn is null ? "Reach out to {person}: no contact logged yet" : "Reach out to {person}: last contact on {date}";
        // The longest overdue first: the due day, oldest first.
        var due = last.AddDays(person.ContactEveryDays ?? 0);
        return new AlertCandidate($"{ReachOutPrefix}{person.Id}", "reach-out", "Time to reach out", text, values,
            AlertSeverity.Neutral, $"/people/{person.Id}", $"2|{due:yyyy-MM-dd}|{person.Name}|{person.Id}");
    }

    /// <summary>The birthday alert of a person whose next birthday is <paramref name="date"/>.</summary>
    public static AlertCandidate Birthday(Person person, DateOnly date, DateOnly today)
    {
        var inDays = date.DayNumber - today.DayNumber;
        var values = new Dictionary<string, AlertValue>
        {
            ["person"] = AlertValue.Plain(person.Name),
            ["date"] = AlertValue.Day(date),
            ["days"] = AlertValue.Count(inDays)
        };
        if (Birthdays.AgeOn(person, date) is { } age)
        {
            values["age"] = AlertValue.Count(age);
        }
        return new AlertCandidate($"{BirthdayPrefix}{person.Id}", "birthday", "Birthday soon",
            inDays == 0 ? "Today is the birthday of {person}" : "The birthday of {person} is on {date}", values,
            AlertSeverity.Neutral, $"/people/{person.Id}", $"1|{date:yyyy-MM-dd}|{person.Name}|{person.Id}");
    }

    /// <summary>The alert of an open loan after its due day.</summary>
    public static AlertCandidate LoanOverdue(Loan loan, string personName, DateOnly today)
    {
        var values = new Dictionary<string, AlertValue>
        {
            ["person"] = AlertValue.Plain(personName),
            ["item"] = AlertValue.Plain(loan.ItemName),
            ["date"] = AlertValue.Day(loan.DueOn!.Value),
            ["days"] = AlertValue.Count(today.DayNumber - loan.DueOn.Value.DayNumber)
        };
        return new AlertCandidate($"{LoanPrefix}{loan.Id}", "loan-overdue",
            loan.Direction == LoanDirection.Lent ? "Lent item not back" : "Borrowed item to return",
            loan.Direction == LoanDirection.Lent
                ? "{person} has your {item} since {date}"
                : "Return the {item} of {person} (due on {date})",
            values, AlertSeverity.Neutral, $"/people/{loan.PersonId}", $"3|{loan.DueOn:yyyy-MM-dd}|{loan.ItemName}|{loan.Id}");
    }
}
