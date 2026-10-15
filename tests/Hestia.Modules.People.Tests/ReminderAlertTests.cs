using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.People.Contracts;
using Hestia.Modules.Today.Contracts;
using Hestia.Testing;

namespace Hestia.Modules.People.Tests;

/// <summary>
/// The People alerts (T20, source <c>People.Reminders</c>): reach out, birthdays in the next 7 days, and overdue loans.
/// </summary>
public class ReminderAlertTests
{
    private static readonly DateOnly Today = PeopleHost.Today;

    [Fact]
    public async Task OverdueReminders_SoonBirthdays_AndOverdueLoans_AreAlerts_OnToday()
    {
        await using var host = PeopleHost.Start();
        var marco = (await host.CreateAsync("Marco", 42, new BirthdayDto(10, 20, 1990))).Person;
        var ana = (await host.CreateAsync("Ana", 30)).Person;
        await host.CreateAsync("Priya", null, new BirthdayDto(10, 22));
        await host.LogContactAsync(marco.Id, Today.AddDays(-50));
        await host.LogContactAsync(ana.Id, Today.AddDays(-10));
        var stove = await host.AddLoanAsync(ana.Id, new LoanRequest("Camping stove", LentOn: Today.AddDays(-20), DueOn: Today.AddDays(-1)));
        await host.AddLoanAsync(ana.Id, new LoanRequest("Tent", DueOn: Today.AddDays(5)));

        var alerts = await Wait.UntilAsync(host.AlertsAsync, a => a.Count == 3);

        Assert.Equal(new[] { $"people.reach-out.{marco.Id}", $"people.birthday.{marco.Id}", $"people.loan-overdue.{stove.Id}" }.Order(),
            alerts.Select(a => a.Id).Order());
        var reachOut = Assert.Single(alerts, a => a.Kind == "reach-out");
        Assert.Equal(("Time to reach out", "Reach out to {person}: last contact on {date}", AlertSeverity.Neutral, $"/people/{marco.Id}"),
            (reachOut.Title, reachOut.Text, reachOut.Severity, reachOut.Link));
        Assert.Equal(AlertValue.Count(50), reachOut.Values["days"]);
        var birthday = Assert.Single(alerts, a => a.Kind == "birthday");
        Assert.Equal(AlertValue.Day(new DateOnly(2026, 10, 20)), birthday.Values["date"]);
        Assert.Equal(AlertValue.Count(36), birthday.Values["age"]);
        var loan = Assert.Single(alerts, a => a.Kind == "loan-overdue");
        Assert.Equal(("{person} has your {item} since {date}", $"/people/{ana.Id}"), (loan.Text, loan.Link));

        var today = await (await host.Client.GetAsync("/api/today?refresh=true")).ReadAsync<TodayDto>();
        Assert.Equal(3, today.Cards.Count(card => card.Module == "People" && card.Type == TodayCardType.Attention));
    }

    [Fact]
    public async Task AContact_AReturn_AndADelete_ResolveTheirAlerts()
    {
        await using var host = PeopleHost.Start();
        var marco = (await host.CreateAsync("Marco", 7)).Person;
        await host.LogContactAsync(marco.Id, Today.AddDays(-30));
        var loan = await host.AddLoanAsync(marco.Id, new LoanRequest("Drill", LentOn: Today.AddDays(-20), DueOn: Today.AddDays(-5)));
        var priya = (await host.CreateAsync("Priya", null, new BirthdayDto(10, 15))).Person;
        await Wait.UntilAsync(host.AlertsAsync, a => a.Count == 3);

        await host.LogContactAsync(marco.Id);
        await host.PutAsync<LoanDto>($"{PeopleHost.People}/loans/{loan.Id}",
            new LoanRequest("Drill", LentOn: Today.AddDays(-20), DueOn: Today.AddDays(-5), ReturnedOn: Today));
        await host.Client.DeleteAsync($"{PeopleHost.People}/{priya.Id}");

        Assert.Empty(await Wait.UntilAsync(host.AlertsAsync, a => a.Count == 0));
    }

    [Fact]
    public async Task AReminderBecomesDue_AsTimePasses()
    {
        await using var host = PeopleHost.Start();
        var person = (await host.CreateAsync("Daniel", 14)).Person;
        await host.LogContactAsync(person.Id);
        Assert.Empty(await host.AlertsAsync());

        host.Clock.Now = PeopleHost.Now.AddDays(15);

        var alert = Assert.Single(await host.AlertsAsync());
        Assert.Equal($"people.reach-out.{person.Id}", alert.Id);
    }
}
