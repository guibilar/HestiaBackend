using System.Net;
using System.Net.Http.Json;
using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Home.Application;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.Today.Contracts;
using Hestia.Testing;

namespace Hestia.Modules.Home.Tests;

/// <summary>
/// The warranty alerts (T22, source <c>Home.Warranty</c>): one Neutral alert for each item whose warranty ends in the next
/// 30 days, and no alert when a document of the item expires on the same day (Documents warns about it).
/// </summary>
public class WarrantyAlertTests
{
    private static readonly DateOnly Today = HomeHost.Today;

    private static async Task<AlertList> AlertsAsync(HomeHost host) =>
        await (await host.Client.GetAsync("/api/alerts?refresh=true")).ReadAsync<AlertList>();

    private static IReadOnlyList<AlertDto> HomeAlerts(AlertList list) => list.Alerts.Where(a => a.Module == "Home").ToList();

    [Fact]
    public async Task AWarrantyThatEndsSoon_IsAnAlert_AndAnAttentionItemOnToday()
    {
        await using var host = HomeHost.Start();
        var soon = await host.CreateAsync("Dishwasher", warrantyUntil: Today.AddDays(12));
        var today = await host.CreateAsync("Kettle", warrantyUntil: Today);
        await host.CreateAsync("Fridge", warrantyUntil: Today.AddDays(31));
        await host.CreateAsync("Laptop", warrantyUntil: Today.AddDays(-1));
        await host.CreateAsync("Rug");

        var list = await Wait.UntilAsync(() => AlertsAsync(host), l => HomeAlerts(l).Count == 2);

        var alerts = HomeAlerts(list);
        Assert.Equal(new[] { $"home.warranty-ending.{today.Id}", $"home.warranty-ending.{soon.Id}" }, alerts.Select(a => a.Id).Order());
        var alert = Assert.Single(alerts, a => a.Id == $"home.warranty-ending.{soon.Id}");
        Assert.Equal(("warranty-ending", "Warranty ends soon", "The warranty of {item} ends on {date}", AlertSeverity.Neutral, $"/home/items/{soon.Id}"),
            (alert.Kind, alert.Title, alert.Text, alert.Severity, alert.Link));
        Assert.Equal(AlertValue.Plain("Dishwasher"), alert.Values["item"]);
        Assert.Equal(AlertValue.Day(Today.AddDays(12)), alert.Values["date"]);
        Assert.Equal(AlertValue.Count(12), alert.Values["days"]);
        Assert.Equal("The warranty of {item} ends today", Assert.Single(alerts, a => a.Id == $"home.warranty-ending.{today.Id}").Text);
        Assert.Equal(AlertModuleStatus.Complete, Assert.Single(list.Modules, m => m.Module == "Home").Status);

        var todayScreen = await (await host.Client.GetAsync("/api/today?refresh=true")).ReadAsync<TodayDto>();
        var items = todayScreen.Cards.Where(card => card.Module == "Home" && card.Type == TodayCardType.Attention).ToList();
        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(todayScreen.Cards, card => card.Module == "Home" && card.Type == TodayCardType.Tile);
    }

    [Fact]
    public async Task ADocumentThatExpiresOnTheSameDay_TakesThePlaceOfTheWarrantyAlert()
    {
        await using var host = HomeHost.Start();
        var until = Today.AddDays(20);
        var warranty = await host.UploadDocumentAsync("Dishwasher warranty", until);
        var other = await host.UploadDocumentAsync("Dishwasher manual", Today.AddDays(200));
        // The item links to its warranty: no double alert.
        await host.CreateAsync(new CreateItemRequest("Dishwasher", WarrantyUntil: until,
            Links: [new CreateItemLinkRequest(ItemLinkKinds.DocumentsDocument, warranty.Id.ToString())]));
        // A document with another date does not take its place.
        var oven = await host.CreateAsync(new CreateItemRequest("Oven", WarrantyUntil: until,
            Links: [new CreateItemLinkRequest(ItemLinkKinds.DocumentsDocument, other.Id.ToString())]));
        // The link from the document side counts too.
        var dryer = await host.CreateAsync("Dryer", warrantyUntil: until);
        var dryerWarranty = await host.UploadDocumentAsync("Dryer warranty", until);
        Assert.Equal(HttpStatusCode.Created, (await host.Client.PostAsJsonAsync($"/api/documents/{dryerWarranty.Id}/links",
            new CreateDocumentLinkRequest("Home.Item", dryer.Id.ToString()))).StatusCode);

        var list = await Wait.UntilAsync(() => AlertsAsync(host),
            l => HomeAlerts(l).Count == 1 && l.Alerts.Count(a => a.Module == "Documents") == 2);

        Assert.Equal($"home.warranty-ending.{oven.Id}", Assert.Single(HomeAlerts(list)).Id);
        Assert.Equal(new[] { $"documents.expiring.{dryerWarranty.Id}", $"documents.expiring.{warranty.Id}" }.Order(),
            list.Alerts.Where(a => a.Module == "Documents").Select(a => a.Id).Order());

        // A new expiry date of the document brings the warranty alert back (DocumentChanged checks all items).
        var update = await host.Client.PutAsJsonAsync($"/api/documents/{dryerWarranty.Id}",
            new UpdateDocumentRequest("Dryer warranty", DocumentKind.Warranty, null, null, until.AddDays(365), null), HestiaApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var after = await Wait.UntilAsync(() => AlertsAsync(host), l => HomeAlerts(l).Count == 2);
        Assert.Contains(HomeAlerts(after), a => a.Id == $"home.warranty-ending.{dryer.Id}");
    }

    [Fact]
    public async Task TheAlert_Resolves_WhenTheWarrantyChanges_OrTheItemIsDeleted()
    {
        await using var host = HomeHost.Start();
        var tv = await host.CreateAsync("TV", warrantyUntil: Today.AddDays(5));
        var radio = await host.CreateAsync("Radio", warrantyUntil: Today.AddDays(6));
        await Wait.UntilAsync(() => AlertsAsync(host), l => HomeAlerts(l).Count == 2);

        await host.Client.PutAsJsonAsync($"{HomeHost.Items}/{tv.Id}", new UpdateItemRequest("TV", WarrantyUntil: Today.AddDays(400)), HestiaApiFactory.Json);
        await host.Client.DeleteAsync($"{HomeHost.Items}/{radio.Id}");

        var list = await Wait.UntilAsync(() => AlertsAsync(host), l => HomeAlerts(l).Count == 0);
        Assert.Empty(HomeAlerts(list));
    }

    [Fact]
    public async Task AWarranty_StartsToEndSoon_AsTimePasses()
    {
        await using var host = HomeHost.Start();
        var item = await host.CreateAsync("Vacuum", warrantyUntil: Today.AddDays(40));
        Assert.Empty(HomeAlerts(await AlertsAsync(host)));

        host.Clock.Now = HomeHost.Now.AddDays(15);

        var list = await Wait.UntilAsync(() => AlertsAsync(host), l => HomeAlerts(l).Count == 1);
        var alert = Assert.Single(HomeAlerts(list));
        Assert.Equal(($"home.warranty-ending.{item.Id}", AlertValue.Count(25)), (alert.Id, alert.Values["days"]));
    }

    [Fact]
    public void TheRule_GivesTheTextAndTheSortKey()
    {
        var id = Guid.NewGuid();

        var alert = WarrantyAlerts.Alert(id, "Kettle", Today.AddDays(3), Today);

        Assert.Equal(($"home.warranty-ending.{id}", "The warranty of {item} ends on {date}", $"2026-10-17|Kettle|{id}"),
            (alert.Key, alert.Text, alert.SortKey));
    }
}
