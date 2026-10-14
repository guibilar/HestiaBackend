using System.Net.Http.Json;
using System.Text.Json;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.Home.Infrastructure;
using Hestia.Platform.Events;
using Hestia.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Home.Tests;

/// <summary>
/// The Home events are durable from the first migration (T38): each event is a row of <c>home.EventOutbox</c>, stored
/// with the change, and the host's sender delivers it, also after a restart.
/// </summary>
public class DurableEventTests
{
    [Fact]
    public async Task EachEvent_ReachesItsConsumers_AndLeavesTheOutbox()
    {
        await using var host = HomeHost.Start();

        var room = await host.CreateRoomAsync("Garage");
        var item = await host.CreateAsync("Lawn mower", room.Id, 450m);
        await host.Client.PutAsJsonAsync($"{HomeHost.Items}/{item.Id}", new UpdateItemRequest("Lawn mower", room.Id, Quantity: 2), HestiaApiFactory.Json);
        await host.Client.DeleteAsync($"{HomeHost.Items}/{item.Id}");
        await host.Client.DeleteAsync($"{HomeHost.Rooms}/{room.Id}");

        await host.Factory.Events.WaitForAsync<RoomChanged>(e => e.RoomId == room.Id && e.Change == RoomChange.Added);
        var added = await host.Factory.Events.WaitForAsync<ItemAdded>(e => e.ItemId == item.Id);
        Assert.Equal(450m, added.Value);
        var changed = await host.Factory.Events.WaitForAsync<ItemChanged>(e => e.ItemId == item.Id);
        Assert.Equal((ItemChange.Details, 2, 0m), (changed.Change, changed.Quantity, changed.Value));
        await host.Factory.Events.WaitForAsync<ItemDeleted>(e => e.ItemId == item.Id);
        await host.Factory.Events.WaitForAsync<RoomChanged>(e => e.RoomId == room.Id && e.Change == RoomChange.Deleted);
        await Wait.UntilAsync(() => OutboxAsync(host.Factory), rows => rows.Count == 0);
        Assert.Empty(await OutboxAsync(host.Factory));
    }

    [Fact]
    public async Task ItemAdded_IsStoredInTheOutbox_WithTheItem_AndSurvivesARestart()
    {
        var clock = new TestClock(HomeHost.Now);
        // The first host commits the item and its event, and stops before it sends the event.
        var first = new HestiaApiFactory(HomeHost.Settings(new Dictionary<string, string?> { ["Events:Outbox:Enabled"] = "false" }),
            HomeHost.Services(clock));
        var response = await first.CreateClient().PostAsJsonAsync(HomeHost.Items, new CreateItemRequest("Sofa"), HestiaApiFactory.Json);
        var item = await response.ReadAsync<ItemDto>();

        var row = Assert.Single(await OutboxAsync(first));
        Assert.Equal(typeof(ItemAdded).FullName, row.EventType);
        var stored = JsonSerializer.Deserialize<ItemAdded>(row.Payload)!;
        Assert.Equal(item.Id, stored.ItemId);
        Assert.Empty(await first.Events.AfterQuietPeriodAsync<ItemAdded>());

        // The next host, on the same database, sends it with the same event id.
        await using var second = first.Restarted(HomeHost.Settings(), HomeHost.Services(clock));
        await first.DisposeAsync();
        second.CreateClient();

        var received = await second.Events.WaitForAsync<ItemAdded>(e => e.ItemId == item.Id);
        Assert.Equal(stored.EventId, received.EventId);
        await Wait.UntilAsync(() => OutboxAsync(second), rows => rows.Count == 0);
    }

    private static Task<List<OutboxMessage>> OutboxAsync(HestiaApiFactory factory) =>
        factory.WithScopeAsync(services =>
            services.GetRequiredService<HomeDbContext>().Set<OutboxMessage>().AsNoTracking().OrderBy(row => row.Id).ToListAsync());
}
