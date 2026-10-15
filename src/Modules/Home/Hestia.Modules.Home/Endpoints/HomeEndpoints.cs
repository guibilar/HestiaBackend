using Hestia.Modules.Home.Application;
using Hestia.Modules.Home.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Hestia.Modules.Home.Endpoints;

/// <summary>The Home endpoints, under /api/home. A read-only user can call the GET endpoints only (also the export).</summary>
public static class HomeEndpoints
{
    public const string Prefix = "/api/home";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var home = endpoints.MapGroup(Prefix).WithTags("Home");

        home.MapGet("/summary", (ItemService service, CancellationToken cancellationToken) => service.SummaryAsync(cancellationToken))
            .WithName("GetHomeSummary")
            .WithSummary("The item count, the total value, the count per room, and the warranties that end in the next 30 days.");

        home.MapGet("/maintenance", (ItemService service, CancellationToken cancellationToken) => service.MaintenanceAsync(cancellationToken))
            .WithName("ListHomeMaintenance")
            .WithSummary("All maintenance: the open tasks linked to an item from either side, by due date (T89).");

        home.MapGet("/categories", (ItemService service, CancellationToken cancellationToken) => service.CategoriesAsync(cancellationToken))
            .WithName("ListHomeCategories")
            .WithSummary("The categories in use, by name, with the count of items in each (T89).");

        MapRooms(home);
        MapItems(home);
    }

    private static void MapRooms(RouteGroupBuilder home)
    {
        home.MapGet("/rooms", (RoomService service, CancellationToken cancellationToken) => service.ListAsync(cancellationToken))
            .WithName("ListRooms")
            .WithSummary("All rooms in list order, with the item count and the total value of each.");

        home.MapPost("/rooms", async Task<Created<RoomDto>> (CreateRoomRequest request, RoomService service,
                CancellationToken cancellationToken) =>
            {
                var room = await service.CreateAsync(request, cancellationToken);
                return TypedResults.Created($"{Prefix}/rooms/{room.Id}", room);
            })
            .WithName("CreateRoom")
            .WithSummary("Adds a room. The name is required and unique (any letter case; 409 for a name that exists).");

        home.MapPut("/rooms/order", (RoomOrderRequest request, RoomService service, CancellationToken cancellationToken) =>
                service.ReorderAsync(request, cancellationToken))
            .WithName("ReorderRooms")
            .WithSummary("A new order of the rooms: { \"roomIds\": [ all room ids, in the new order ] }.");

        home.MapPut("/rooms/{id:guid}", (Guid id, UpdateRoomRequest request, RoomService service, CancellationToken cancellationToken) =>
                service.UpdateAsync(id, request, cancellationToken))
            .WithName("UpdateRoom")
            .WithSummary("Renames a room, or moves it in the list (sortOrder).");

        home.MapDelete("/rooms/{id:guid}", async Task<NoContent> (Guid id, RoomService service, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(id, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("DeleteRoom")
            .WithSummary("Deletes a room without items. A room with items gets 409.");
    }

    private static void MapItems(RouteGroupBuilder home)
    {
        home.MapPost("/items", async Task<Created<ItemDto>> (CreateItemRequest request, ItemService service,
                CancellationToken cancellationToken) =>
            {
                var item = await service.CreateAsync(request, cancellationToken);
                return TypedResults.Created($"{Prefix}/items/{item.Id}", item);
            })
            .WithName("CreateItem")
            .WithSummary("Adds an item. Only the name is required.")
            .WithDescription(
                "Amounts are plain decimals in the one app currency, for one item (the value of the row is multiplied by " +
                "quantity). purchasedOn and warrantyUntil are days (yyyy-MM-dd). links are { kind, targetId } " +
                "(Documents.Document, Money.Transaction, or Tasks.Task); the host checks that each one exists.");

        home.MapGet("/items", (ItemService service, CancellationToken cancellationToken, string? room, string? category,
                string? tag, string? q, string? warranty, int page = 1, int pageSize = 20) =>
                service.ListAsync(new ItemQuery(room, category, tag, q, warranty, page, pageSize), cancellationToken))
            .WithName("ListItems")
            .WithSummary("A page of items, by name (with q: best match first).")
            .WithDescription(
                "Filters: room (a room id, or none), category (any letter case), tag, q (full-text search in the name, the " +
                "brand, the model, the serial number, the category, the notes, and the tags), warranty (active: ends today " +
                "or later; expiring: ends in the next 30 days; expired: ended before today).");

        home.MapGet("/items/export.csv", async (InventoryExport export, CancellationToken cancellationToken,
                string? room, string? category, string? tag, string? q, string? warranty) =>
            {
                var file = await export.ExportAsync(new ItemQuery(room, category, tag, q, warranty), cancellationToken);
                return TypedResults.File(file.Content, InventoryExport.ContentType, file.FileName);
            })
            .WithName("ExportItems")
            .WithSummary("All items (or the items of the same filters as the list) as a CSV file for an insurance claim.")
            .WithDescription(
                "UTF-8 with a byte order mark. Columns: Room, Name, Category, Brand, Model, Serial number, Quantity, " +
                "Purchase date, Purchase price, Current value, Warranty until, Documents (the titles, with \"; \").")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv");

        home.MapGet("/items/by-link", (ItemService service, CancellationToken cancellationToken, string? kind, string? id) =>
                service.ListByLinkAsync(kind, id, cancellationToken))
            .WithName("ListItemsByLink")
            .WithSummary("The items of one thing, for example ?kind=Documents.Document&id=<document id>.")
            .WithDescription("Also the items that the document or the task links to (kind Home.Item).");

        home.MapGet("/items/{id:guid}", (Guid id, ItemService service, CancellationToken cancellationToken) =>
                service.GetAsync(id, cancellationToken))
            .WithName("GetItem")
            .WithSummary("One item, with its links, its documents, and its maintenance tasks.");

        home.MapPut("/items/{id:guid}", (Guid id, UpdateItemRequest request, ItemService service, CancellationToken cancellationToken) =>
                service.UpdateAsync(id, request, cancellationToken))
            .WithName("UpdateItem")
            .WithSummary("Changes the details of an item (all of them). The links stay.");

        home.MapDelete("/items/{id:guid}", async Task<NoContent> (Guid id, ItemService service, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(id, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("DeleteItem")
            .WithSummary("Deletes an item and its links. Documents and tasks lose their links to it.");

        home.MapPost("/items/{id:guid}/links", async Task<Created<ItemLinkDto>> (Guid id, CreateItemLinkRequest request,
                ItemService service, CancellationToken cancellationToken) =>
            {
                var link = await service.AddLinkAsync(id, request, cancellationToken);
                return TypedResults.Created($"{Prefix}/items/{id}/links/{link.Id}", link);
            })
            .WithName("AddItemLink")
            .WithSummary("Links the item to a document, a task, or a finance transaction, for example { \"kind\": \"Documents.Document\", \"targetId\": \"<id>\" }.")
            .WithDescription("The host checks that the thing exists. For a transaction it asks finance as the signed-in user (502 when finance does not answer).");

        home.MapDelete("/items/{id:guid}/links/{linkId:guid}", async Task<NoContent> (Guid id, Guid linkId, ItemService service,
                CancellationToken cancellationToken) =>
            {
                await service.RemoveLinkAsync(id, linkId, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("RemoveItemLink")
            .WithSummary("Removes a link of the item.");
    }
}
