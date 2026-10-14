using System.Net;
using System.Net.Http.Json;
using Hestia.Modules.Home.Contracts;
using Hestia.Testing;

namespace Hestia.Modules.Home.Tests;

/// <summary>The rooms (T22): add, rename, order, delete (only without items), and the item count and value of each.</summary>
public class RoomTests
{
    private const string Rooms = HomeHost.Rooms;

    [Fact]
    public async Task TheModule_StartsWithNoRooms_AndAddsThemInOrder()
    {
        await using var host = HomeHost.Start();

        Assert.Empty(await host.RoomsAsync());
        var kitchen = await host.CreateRoomAsync("  Kitchen ");
        var office = await host.CreateRoomAsync("Office");

        Assert.Equal(("Kitchen", 1, 0, 0m), (kitchen.Name, kitchen.SortOrder, kitchen.ItemCount, kitchen.TotalValue));
        Assert.Equal(2, office.SortOrder);
        Assert.Equal(new[] { "Kitchen", "Office" }, (await host.RoomsAsync()).Select(r => r.Name));
        var added = await host.Factory.Events.WaitForAsync<RoomChanged>(e => e.RoomId == office.Id);
        Assert.Equal((RoomChange.Added, "Office"), (added.Change, added.Name));
    }

    [Fact]
    public async Task ARoomName_IsRequired_Short_AndUnique()
    {
        await using var host = HomeHost.Start();
        var kitchen = await host.CreateRoomAsync("Kitchen");
        var office = await host.CreateRoomAsync("Office");

        await HomeHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(Rooms, new CreateRoomRequest(" ")), "name");
        await HomeHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(Rooms, new CreateRoomRequest(new string('a', 101))), "name");
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsJsonAsync(Rooms, new CreateRoomRequest("KITCHEN"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PutAsJsonAsync($"{Rooms}/{office.Id}", new UpdateRoomRequest("kitchen"))).StatusCode);
        // A new letter case of its own name is a rename.
        var renamed = await (await host.Client.PutAsJsonAsync($"{Rooms}/{kitchen.Id}", new UpdateRoomRequest("KITCHEN"))).ReadAsync<RoomDto>();
        Assert.Equal("KITCHEN", renamed.Name);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PutAsJsonAsync($"{Rooms}/{Guid.NewGuid()}", new UpdateRoomRequest("Attic"))).StatusCode);
    }

    [Fact]
    public async Task Rename_RaisesRoomChanged_WithTheOldName_AndKeepsThePlace()
    {
        await using var host = HomeHost.Start();
        var room = await host.CreateRoomAsync("Living room");

        var response = await host.Client.PutAsJsonAsync($"{Rooms}/{room.Id}", new UpdateRoomRequest("Lounge"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var renamed = await response.ReadAsync<RoomDto>();
        Assert.Equal(("Lounge", room.SortOrder), (renamed.Name, renamed.SortOrder));
        var changed = await host.Factory.Events.WaitForAsync<RoomChanged>(e => e.Change == RoomChange.Renamed);
        Assert.Equal((room.Id, "Lounge", "Living room"), (changed.RoomId, changed.Name, changed.OldName));
    }

    [Fact]
    public async Task ARoomWithItems_CannotBeDeleted_AnEmptyRoomCan()
    {
        await using var host = HomeHost.Start();
        var kitchen = await host.CreateRoomAsync("Kitchen");
        var item = await host.CreateAsync("Dishwasher", kitchen.Id);

        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.DeleteAsync($"{Rooms}/{kitchen.Id}")).StatusCode);

        await host.Client.PutAsJsonAsync($"{HomeHost.Items}/{item.Id}", new UpdateItemRequest("Dishwasher"), HestiaApiFactory.Json);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{Rooms}/{kitchen.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync($"{Rooms}/{kitchen.Id}")).StatusCode);
        Assert.Empty(await host.RoomsAsync());
        var deleted = await host.Factory.Events.WaitForAsync<RoomChanged>(e => e.Change == RoomChange.Deleted);
        Assert.Equal(("Kitchen", kitchen.Id), (deleted.Name, deleted.RoomId));
    }

    [Fact]
    public async Task Order_SetsTheOrderOfAllRooms()
    {
        await using var host = HomeHost.Start();
        var kitchen = await host.CreateRoomAsync("Kitchen");
        var office = await host.CreateRoomAsync("Office");
        var garage = await host.CreateRoomAsync("Garage");

        var response = await host.Client.PutAsJsonAsync($"{Rooms}/order", new RoomOrderRequest([garage.Id, kitchen.Id, office.Id]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "Garage", "Kitchen", "Office" }, (await response.ReadAsync<List<RoomDto>>()).Select(r => r.Name));
        Assert.Equal(new[] { "Garage", "Kitchen", "Office" }, (await host.RoomsAsync()).Select(r => r.Name));

        // All rooms, each one time, and only rooms that exist.
        await HomeHost.AssertBadRequestAsync(await host.Client.PutAsJsonAsync($"{Rooms}/order", new RoomOrderRequest([garage.Id, kitchen.Id])), "roomIds");
        await HomeHost.AssertBadRequestAsync(await host.Client.PutAsJsonAsync($"{Rooms}/order",
            new RoomOrderRequest([garage.Id, kitchen.Id, kitchen.Id])), "roomIds");
        await HomeHost.AssertBadRequestAsync(await host.Client.PutAsJsonAsync($"{Rooms}/order",
            new RoomOrderRequest([garage.Id, kitchen.Id, Guid.NewGuid()])), "roomIds");
        await HomeHost.AssertBadRequestAsync(await host.Client.PutAsJsonAsync($"{Rooms}/order", new RoomOrderRequest(null)), "roomIds");
    }

    [Fact]
    public async Task EachRoom_HasItsItemCount_AndTheTotalValue()
    {
        await using var host = HomeHost.Start();
        var kitchen = await host.CreateRoomAsync("Kitchen");
        var office = await host.CreateRoomAsync("Office");
        // Purchase price 899.99, current value 600: the value is 600.
        await host.CreateAsync(new CreateItemRequest("Dishwasher", RoomId: kitchen.Id, PurchasePrice: 899.99m, CurrentValue: 600m));
        // Four chairs of 75.50 each.
        await host.CreateAsync(new CreateItemRequest("Chair", RoomId: kitchen.Id, Quantity: 4, PurchasePrice: 75.50m));
        // No price: value 0.
        await host.CreateAsync(new CreateItemRequest("Fruit bowl", RoomId: kitchen.Id));
        await host.CreateAsync(new CreateItemRequest("Laptop", RoomId: office.Id, PurchasePrice: 1500m));
        await host.CreateAsync(new CreateItemRequest("Bike", PurchasePrice: 400m));

        var rooms = await host.RoomsAsync();

        Assert.Equal(new[] { ("Kitchen", 3, 902.00m), ("Office", 1, 1500m) }, rooms.Select(r => (r.Name, r.ItemCount, r.TotalValue)));
    }
}
