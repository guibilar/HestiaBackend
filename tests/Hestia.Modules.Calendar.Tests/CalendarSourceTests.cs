using System.Net.Http.Json;
using Hestia.Modules.Calendar.Contracts;
using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Documents.Infrastructure;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.People.Contracts;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Calendar.Tests;

/// <summary>T103: the dated things of the other modules are on the Calendar, each on its day, with a link.</summary>
public class CalendarSourceTests
{
    private static readonly DateOnly Today = CalendarHost.Today;

    [Fact]
    public async Task TasksBirthdaysReminders_LoansExpiries_AndWarranties_AreOnTheCalendar()
    {
        await using var host = CalendarHost.Start();
        var client = host.Client;
        var json = HestiaApiFactory.Json;

        var task = await (await client.PostAsJsonAsync("/api/tasks", new CreateTaskRequest("Change the furnace filter", DueOn: Today.AddDays(3)), json))
            .ReadAsync<TaskDto>();
        // A task that is not due in the range, and a done one, are not there.
        await client.PostAsJsonAsync("/api/tasks", new CreateTaskRequest("Later", DueOn: Today.AddDays(60)), json);

        var person = (await (await client.PostAsJsonAsync("/api/people", new CreatePersonRequest("Ana", Birthday: new BirthdayDto(10, 20, 1990),
            ContactEveryDays: 10), json)).ReadAsync<PersonDto>()).Person;
        await client.PostAsJsonAsync($"/api/people/{person.Id}/loans",
            new LoanRequest("Camping stove", LentOn: Today.AddDays(-5), DueOn: Today.AddDays(5)), json);

        var room = await (await client.PostAsJsonAsync("/api/home/rooms", new CreateRoomRequest("Kitchen"), json)).ReadAsync<RoomDto>();
        var item = await (await client.PostAsJsonAsync("/api/home/items", new CreateItemRequest("Dishwasher", room.Id,
            WarrantyUntil: Today.AddDays(12)), json)).ReadAsync<ItemDto>();

        var documentId = await host.Factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<DocumentsDbContext>();
            var document = new Documents.Domain.Document
            {
                Id = Guid.NewGuid(), Title = "Lease", Kind = DocumentKind.Lease, ExpiresOn = Today.AddDays(20),
                CreatedAt = CalendarHost.Now, UpdatedAt = CalendarHost.Now, OcrStatus = OcrStatus.NotNeeded
            };
            db.Documents.Add(document);
            await db.SaveChangesAsync();
            return document.Id;
        });

        var view = await host.ViewAsync(Today, Today.AddDays(30));

        var others = view.Items.Where(i => i.Module != "Calendar").ToList();
        Assert.Equal(
            [
                ("Tasks", "task-due", "Change the furnace filter", Today.AddDays(3), $"/tasks/{task.Id:D}"),
                ("People", "birthday", "Ana's birthday", new DateOnly(2026, 10, 20), $"/people/{person.Id:D}"),
                ("People", "reach-out", "Reach out to Ana", Today.AddDays(10), $"/people/{person.Id:D}"),
                ("People", "loan-due", "Camping stove back from Ana", Today.AddDays(5), $"/people/{person.Id:D}"),
                ("Home", "warranty-ends", "Warranty of Dishwasher ends", Today.AddDays(12), $"/home/items/{item.Id:D}"),
                ("Documents", "document-expires", "Lease expires", Today.AddDays(20), $"/documents/{documentId:D}")
            ],
            others.OrderBy(i => i.Module == "Tasks" ? 0 : i.Module == "People" ? 1 : i.Module == "Home" ? 2 : 3)
                .ThenBy(i => i.Kind == "birthday" ? 0 : i.Kind == "reach-out" ? 1 : 2)
                .Select(i => (i.Module, i.Kind, i.Title, i.StartDate, i.Link!)));
        Assert.Equal("Turns 36", others.Single(i => i.Kind == "birthday").Text);
        Assert.False(others.Single(i => i.Kind == "task-due").Done);
        Assert.All(others, i => Assert.True(i.AllDay));
        Assert.All(view.Modules, m => Assert.Equal(CalendarModuleStatus.Complete, m.Status));
        Assert.Equal(["Calendar", "Documents", "Home", "People", "Tasks"], view.Modules.Select(m => m.Module));
        // In date order.
        Assert.Equal(view.Items.Select(i => i.StartDate).Order(), view.Items.Select(i => i.StartDate));
    }

    [Fact]
    public async Task ABirthday_IsThereEachYearOfALongRange()
    {
        await using var host = CalendarHost.Start();
        await host.Client.PostAsJsonAsync("/api/people", new CreatePersonRequest("Leap", Birthday: new BirthdayDto(2, 29)), HestiaApiFactory.Json);

        var view = await host.ViewAsync(new DateOnly(2027, 2, 1), new DateOnly(2028, 3, 1));

        // February 29th is February 28th in a year without it.
        Assert.Equal([new DateOnly(2027, 2, 28), new DateOnly(2028, 2, 29)],
            view.Items.Where(i => i.Kind == "birthday").Select(i => i.StartDate));
    }
}
