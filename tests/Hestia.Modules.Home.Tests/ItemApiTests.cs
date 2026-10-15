using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.Home.Infrastructure;
using Hestia.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Home.Tests;

/// <summary>The item endpoints (T22): add, list with filters and search, get, change, delete, and the summary.</summary>
public class ItemApiTests
{
    private const string Items = HomeHost.Items;
    private static readonly DateOnly Today = HomeHost.Today;

    [Fact]
    public async Task Categories_ListsTheCategoriesInUse_WithTheirCounts()
    {
        await using var host = HomeHost.Start();
        Assert.Empty(await (await host.Client.GetAsync("/api/home/categories")).ReadAsync<List<CategoryCountDto>>());
        await host.CreateAsync(new CreateItemRequest("Dishwasher", Category: "Appliance"));
        await host.CreateAsync(new CreateItemRequest("Fridge", Category: "Appliance"));
        await host.CreateAsync(new CreateItemRequest("Sofa", Category: "Furniture"));
        await host.CreateAsync(new CreateItemRequest("Box of cables"));

        var categories = await (await host.Client.GetAsync("/api/home/categories")).ReadAsync<List<CategoryCountDto>>();

        Assert.Equal(new[] { new CategoryCountDto("Appliance", 2), new CategoryCountDto("Furniture", 1) }, categories);
    }

    [Fact]
    public async Task Create_StoresAllDetails_AndRaisesItemAdded()
    {
        await using var host = HomeHost.Start();
        var kitchen = await host.CreateRoomAsync("Kitchen");

        var item = await host.CreateAsync(new CreateItemRequest(" Dishwasher ", kitchen.Id, "Appliance", "Bosch", "SHX 300",
            "SN-4711-AB", 1, new DateOnly(2025, 3, 1), 899.995m, null, new DateOnly(2027, 3, 1), "Bought at the store.",
            ["Kitchen", "appliance, Warranty"]));

        Assert.Equal(("Dishwasher", (Guid?)kitchen.Id, "Kitchen", "Appliance", "Bosch", "SHX 300", "SN-4711-AB", 1),
            (item.Name, item.RoomId, item.RoomName, item.Category, item.Brand, item.Model, item.SerialNumber, item.Quantity));
        // Amounts are rounded to cents.
        Assert.Equal((900.00m, (decimal?)null, 900.00m), (item.PurchasePrice!.Value, item.CurrentValue, item.Value));
        Assert.Equal((new DateOnly(2025, 3, 1), new DateOnly(2027, 3, 1)), (item.PurchasedOn!.Value, item.WarrantyUntil!.Value));
        Assert.Equal(new[] { "kitchen", "appliance", "warranty" }, item.Tags);
        Assert.Empty(item.Links);
        Assert.Empty(item.Documents);
        Assert.Empty(item.Maintenance);

        var added = await host.Factory.Events.WaitForAsync<ItemAdded>(e => e.ItemId == item.Id);
        Assert.Equal(("Dishwasher", (Guid?)kitchen.Id, 900.00m, 1), (added.Name, added.RoomId, added.Value, added.Quantity));
        Assert.Equal(item, await host.GetAsync(item.Id), ItemComparer.Instance);
    }

    [Fact]
    public async Task Create_RefusesBadValues()
    {
        await using var host = HomeHost.Start();

        await HomeHost.AssertBadRequestAsync(await Post(host, new CreateItemRequest("  ")), "name");
        await HomeHost.AssertBadRequestAsync(await Post(host, new CreateItemRequest(new string('n', 201))), "name");
        await HomeHost.AssertBadRequestAsync(await Post(host, new CreateItemRequest("Chair", Quantity: 0)), "quantity");
        await HomeHost.AssertBadRequestAsync(await Post(host, new CreateItemRequest("Chair", PurchasePrice: -1)), "purchasePrice");
        await HomeHost.AssertBadRequestAsync(await Post(host, new CreateItemRequest("Chair", CurrentValue: 10_000_000_000m)), "currentValue");
        await HomeHost.AssertBadRequestAsync(await Post(host, new CreateItemRequest("Chair", Category: new string('c', 51))), "category");
        await HomeHost.AssertBadRequestAsync(await Post(host, new CreateItemRequest("Chair", SerialNumber: new string('s', 101))), "serialNumber");
        await HomeHost.AssertBadRequestAsync(await Post(host, new CreateItemRequest("Chair", RoomId: Guid.NewGuid())), "roomId");
        await HomeHost.AssertBadRequestAsync(await Post(host, new CreateItemRequest("Chair", Tags: ["a|b"])), "tags");
        Assert.Equal(0, await host.WithDbAsync(db => db.Items.CountAsync()));
    }

    [Fact]
    public async Task Update_ReplacesTheDetails_AndRaisesItemChanged()
    {
        await using var host = HomeHost.Start();
        var office = await host.CreateRoomAsync("Office");
        var item = await host.CreateAsync(new CreateItemRequest("Laptop", Brand: "Lenovo", PurchasePrice: 1500m, Tags: ["work"]));

        var response = await host.Client.PutAsJsonAsync($"{Items}/{item.Id}",
            new UpdateItemRequest("Laptop", office.Id, "Electronics", Quantity: 2, PurchasePrice: 1500m, CurrentValue: 700m), HestiaApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changed = await response.ReadAsync<ItemDto>();
        Assert.Equal(((Guid?)office.Id, "Office", "Electronics", (string?)null, 2, 1400m), (changed.RoomId, changed.RoomName, changed.Category,
            changed.Brand, changed.Quantity, changed.Value));
        Assert.Empty(changed.Tags);
        var raised = await host.Factory.Events.WaitForAsync<ItemChanged>(e => e.ItemId == item.Id);
        Assert.Equal((ItemChange.Details, (Guid?)office.Id, 1400m), (raised.Change, raised.RoomId, raised.Value));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PutAsJsonAsync($"{Items}/{Guid.NewGuid()}", new UpdateItemRequest("X"))).StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesTheItem_AndRaisesItemDeleted()
    {
        await using var host = HomeHost.Start();
        var item = await host.CreateAsync("Old TV");

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{Items}/{item.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"{Items}/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync($"{Items}/{item.Id}")).StatusCode);
        var deleted = await host.Factory.Events.WaitForAsync<ItemDeleted>(e => e.ItemId == item.Id);
        Assert.Equal("Old TV", deleted.Name);
    }

    [Fact]
    public async Task TheList_FiltersByRoom_Category_Tag_AndWarranty()
    {
        await using var host = HomeHost.Start();
        var kitchen = await host.CreateRoomAsync("Kitchen");
        var office = await host.CreateRoomAsync("Office");
        await host.CreateAsync(new CreateItemRequest("Dishwasher", kitchen.Id, "Appliance", WarrantyUntil: Today.AddDays(10), Tags: ["insured"]));
        await host.CreateAsync(new CreateItemRequest("Fridge", kitchen.Id, "appliance", WarrantyUntil: Today.AddDays(400)));
        await host.CreateAsync(new CreateItemRequest("Laptop", office.Id, "Electronics", WarrantyUntil: Today.AddDays(-1), Tags: ["insured"]));
        await host.CreateAsync(new CreateItemRequest("Bike", WarrantyUntil: Today));
        await host.CreateAsync(new CreateItemRequest("Rug"));

        async Task<string[]> Names(string query) => (await host.ListAsync(query)).Items.Select(i => i.Name).ToArray();

        Assert.Equal(new[] { "Bike", "Dishwasher", "Fridge", "Laptop", "Rug" }, await Names(""));
        Assert.Equal(new[] { "Dishwasher", "Fridge" }, await Names($"room={kitchen.Id}"));
        Assert.Equal(new[] { "Bike", "Rug" }, await Names("room=none"));
        Assert.Equal(new[] { "Dishwasher", "Fridge" }, await Names("category=APPLIANCE"));
        Assert.Equal(new[] { "Dishwasher", "Laptop" }, await Names("tag=Insured"));
        Assert.Equal(new[] { "Bike", "Dishwasher", "Fridge" }, await Names("warranty=active"));
        Assert.Equal(new[] { "Bike", "Dishwasher" }, await Names("warranty=expiring"));
        Assert.Equal(new[] { "Laptop" }, await Names("warranty=expired"));
        Assert.Equal(new[] { "Dishwasher" }, await Names($"room={kitchen.Id}&tag=insured&warranty=expiring"));

        var page = await host.ListAsync("page=2&pageSize=2");
        Assert.Equal((5, 3), (page.TotalCount, page.TotalPages));
        Assert.Equal(new[] { "Fridge", "Laptop" }, page.Items.Select(i => i.Name));

        await HomeHost.AssertBadRequestAsync(await host.Client.GetAsync($"{Items}?room=kitchen"), "room");
        await HomeHost.AssertBadRequestAsync(await host.Client.GetAsync($"{Items}?warranty=soon"), "warranty");
        await HomeHost.AssertBadRequestAsync(await host.Client.GetAsync($"{Items}?pageSize=101"), "pageSize");
        await HomeHost.AssertBadRequestAsync(await host.Client.GetAsync($"{Items}?q={new string('q', 201)}"), "q");
    }

    [Fact]
    public async Task Search_FindsWordsInTheName_Brand_Model_SerialNumber_Category_Notes_AndTags()
    {
        await using var host = HomeHost.Start();
        await host.CreateAsync(new CreateItemRequest("Dishwasher", Brand: "Bosch", Model: "SHX300", SerialNumber: "FD9912XK"));
        await host.CreateAsync(new CreateItemRequest("Office chair", Category: "Furniture", Notes: "Ergonomic, from the old flat."));
        await host.CreateAsync(new CreateItemRequest("Camera", Tags: ["photography"]));

        async Task<string[]> Search(string q) => (await host.ListAsync($"q={Uri.EscapeDataString(q)}")).Items.Select(i => i.Name).ToArray();

        Assert.Equal(new[] { "Dishwasher" }, await Search("bosch"));
        Assert.Equal(new[] { "Dishwasher" }, await Search("shx300"));
        Assert.Equal(new[] { "Dishwasher" }, await Search("FD9912XK"));
        Assert.Equal(new[] { "Office chair" }, await Search("furniture"));
        Assert.Equal(new[] { "Office chair" }, await Search("ergonomic"));
        Assert.Equal(new[] { "Office chair" }, await Search("chair"));
        Assert.Equal(new[] { "Camera" }, await Search("photography"));
        Assert.Empty(await Search("toaster"));
    }

    [Fact]
    public async Task Summary_CountsTheItems_TheValue_EachRoom_AndTheWarrantiesThatEndSoon()
    {
        await using var host = HomeHost.Start();
        var kitchen = await host.CreateRoomAsync("Kitchen");
        var office = await host.CreateRoomAsync("Office");
        await host.CreateRoomAsync("Attic");
        await host.CreateAsync(new CreateItemRequest("Dishwasher", kitchen.Id, PurchasePrice: 900m, WarrantyUntil: Today.AddDays(30)));
        await host.CreateAsync(new CreateItemRequest("Fridge", kitchen.Id, PurchasePrice: 1200m, CurrentValue: 1000m, WarrantyUntil: Today.AddDays(31)));
        await host.CreateAsync(new CreateItemRequest("Laptop", office.Id, PurchasePrice: 1500m, WarrantyUntil: Today));
        await host.CreateAsync(new CreateItemRequest("Chair", Quantity: 2, PurchasePrice: 50m, WarrantyUntil: Today.AddDays(-1)));

        var summary = await host.SummaryAsync();

        Assert.Equal((Today, 4, 3500m, 2), (summary.Date, summary.ItemCount, summary.TotalValue, summary.WarrantiesEnding));
        Assert.Equal(new RoomCountDto[]
        {
            new(kitchen.Id, "Kitchen", 2, 1900m),
            new(office.Id, "Office", 1, 1500m),
            new(summary.Rooms[2].RoomId, "Attic", 0, 0m),
            new(null, null, 1, 100m)
        }, summary.Rooms);
    }

    [Fact]
    public async Task WithoutSignIn_TheEndpointsAnswer401()
    {
        await using var host = HomeHost.Start(signedIn: false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(Items)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync($"{Items}/export.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync($"{HomeHost.Home}/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsJsonAsync(HomeHost.Rooms, new CreateRoomRequest("Kitchen"))).StatusCode);
    }

    [Fact]
    public async Task AReadOnlyUser_CanRead_AndExport_ButNotWrite()
    {
        await using var host = HomeHost.Start();
        var room = await host.CreateRoomAsync("Kitchen");
        var item = await host.CreateAsync("Kettle", room.Id);
        var readOnly = host.Factory.CreateClient();
        readOnly.DefaultRequestHeaders.Add(TestSignIn.ReadOnlyHeader, "yes");

        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync(Items)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"{Items}/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"{Items}/export.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"{Items}/by-link?kind=Documents.Document&id={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync(HomeHost.Rooms)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"{HomeHost.Home}/summary")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync(Items, new CreateItemRequest("New"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PutAsJsonAsync($"{Items}/{item.Id}", new UpdateItemRequest("Changed"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.DeleteAsync($"{Items}/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync($"{Items}/{item.Id}/links",
            new CreateItemLinkRequest(ItemLinkKinds.DocumentsDocument, Guid.NewGuid().ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync(HomeHost.Rooms, new CreateRoomRequest("Office"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PutAsJsonAsync($"{HomeHost.Rooms}/order", new RoomOrderRequest([room.Id]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.DeleteAsync($"{HomeHost.Rooms}/{room.Id}")).StatusCode);

        Assert.Equal("Kettle", (await host.GetAsync(item.Id)).Name);
    }

    [Fact]
    public async Task TheOpenApiDocument_DescribesTheHomeEndpoints()
    {
        await using var host = HomeHost.Start();

        var response = await host.Client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.Equal("CreateItem", paths.GetProperty(Items).GetProperty("post").GetProperty("operationId").GetString());
        Assert.Equal("ListItems", paths.GetProperty(Items).GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("ExportItems", paths.GetProperty($"{Items}/export.csv").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("ReorderRooms", paths.GetProperty($"{HomeHost.Rooms}/order").GetProperty("put").GetProperty("operationId").GetString());
        Assert.True(paths.TryGetProperty($"{HomeHost.Home}/summary", out _));
        Assert.True(paths.TryGetProperty($"{Items}/by-link", out _));
        Assert.True(paths.TryGetProperty($"{Items}/{{id}}/links/{{linkId}}", out _));
        Assert.True(paths.TryGetProperty($"{HomeHost.Rooms}/{{id}}", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("ItemDto", out _));
        Assert.True(schemas.TryGetProperty("RoomDto", out _));
        Assert.True(schemas.TryGetProperty("HomeSummaryDto", out _));
    }

    [Fact]
    public async Task Migrations_PutTheTablesAndTheirHistory_InTheHomeSchema()
    {
        Assert.SkipUnless(TestDatabase.OnPostgres, $"Needs PostgreSQL: set {TestDatabase.Variable}.");
        await using var host = HomeHost.Start();
        host.Factory.CreateClient();

        var tables = await host.Factory.WithScopeAsync(services =>
            services.GetRequiredService<HomeDbContext>().Database
                .SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'home'")
                .ToListAsync());

        Assert.Equal(new[] { "EventInbox", "EventOutbox", "ItemLinks", "Items", "Rooms", "__EFMigrationsHistory" },
            tables.Order(StringComparer.Ordinal));
    }

    private static Task<HttpResponseMessage> Post(HomeHost host, CreateItemRequest request) =>
        host.Client.PostAsJsonAsync(Items, request, HestiaApiFactory.Json);

    /// <summary>Compares two items by their values (the lists inside a record compare by reference).</summary>
    private sealed class ItemComparer : IEqualityComparer<ItemDto>
    {
        public static readonly ItemComparer Instance = new();

        public bool Equals(ItemDto? x, ItemDto? y) =>
            JsonSerializer.Serialize(x, HestiaApiFactory.Json) == JsonSerializer.Serialize(y, HestiaApiFactory.Json);

        public int GetHashCode(ItemDto obj) => obj.Id.GetHashCode();
    }
}
