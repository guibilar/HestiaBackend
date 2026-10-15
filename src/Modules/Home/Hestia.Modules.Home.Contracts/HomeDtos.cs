namespace Hestia.Modules.Home.Contracts;

/// <summary>A room of the home, with its item count and the total value of its items.</summary>
/// <param name="Id">The room.</param>
/// <param name="Name">The name (unique, at most 100 characters).</param>
/// <param name="SortOrder">The place of the room in the list: lower first.</param>
/// <param name="ItemCount">How many items (rows) the room has.</param>
/// <param name="TotalValue">The total value of the items in the room (see <see cref="ItemSummaryDto.Value"/>).</param>
/// <param name="CreatedAt">When the room was added.</param>
/// <param name="UpdatedAt">When the room last changed.</param>
public sealed record RoomDto(
    Guid Id,
    string Name,
    int SortOrder,
    int ItemCount,
    decimal TotalValue,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A new room (<c>POST /api/home/rooms</c>).</summary>
/// <param name="Name">The name (required, unique, at most 100 characters).</param>
/// <param name="SortOrder">The place in the list. Without it, the room goes after the last room.</param>
public sealed record CreateRoomRequest(string? Name, int? SortOrder = null);

/// <summary>The new details of a room (<c>PUT /api/home/rooms/{id}</c>).</summary>
/// <param name="Name">The name (required).</param>
/// <param name="SortOrder">The place in the list. Without it, the place does not change.</param>
public sealed record UpdateRoomRequest(string? Name, int? SortOrder = null);

/// <summary>A new order of all rooms (<c>PUT /api/home/rooms/order</c>).</summary>
/// <param name="RoomIds">The ids of all rooms, each one time, in the new order.</param>
public sealed record RoomOrderRequest(IReadOnlyList<Guid>? RoomIds);

/// <summary>A link from an item to something outside the Home module, for example its receipt.</summary>
/// <param name="Id">The link.</param>
/// <param name="Kind">What the link points to, for example <c>Documents.Document</c> (<see cref="ItemLinkKinds"/>).</param>
/// <param name="TargetId">The id of the thing, as text.</param>
/// <param name="CreatedAt">When the link was made.</param>
public sealed record ItemLinkDto(Guid Id, string Kind, string TargetId, DateTimeOffset CreatedAt);

/// <summary>An item in a list.</summary>
/// <param name="Id">The item.</param>
/// <param name="Name">The name.</param>
/// <param name="RoomId">The room, or null.</param>
/// <param name="RoomName">The name of the room, or null.</param>
/// <param name="Category">Free text, for example "Appliance", or null.</param>
/// <param name="Brand">The brand, or null.</param>
/// <param name="Model">The model, or null.</param>
/// <param name="SerialNumber">The serial number, or null.</param>
/// <param name="Quantity">How many of this item the home has (1 or more).</param>
/// <param name="PurchasedOn">The day of the purchase, or null.</param>
/// <param name="PurchasePrice">The price of one item, or null.</param>
/// <param name="CurrentValue">The value of one item now, or null.</param>
/// <param name="Value">
/// The value of the row: the current value (else the purchase price, else 0) of one item, multiplied by the quantity.
/// The totals and the export use it.
/// </param>
/// <param name="WarrantyUntil">The last day of the warranty, or null.</param>
/// <param name="Notes">Free text, or null.</param>
/// <param name="Tags">The tags, in lower case.</param>
/// <param name="CreatedAt">When the item was added.</param>
/// <param name="UpdatedAt">When the item last changed.</param>
/// <param name="Links">The links of the item, oldest first.</param>
public sealed record ItemSummaryDto(
    Guid Id,
    string Name,
    Guid? RoomId,
    string? RoomName,
    string? Category,
    string? Brand,
    string? Model,
    string? SerialNumber,
    int Quantity,
    DateOnly? PurchasedOn,
    decimal? PurchasePrice,
    decimal? CurrentValue,
    decimal Value,
    DateOnly? WarrantyUntil,
    string? Notes,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ItemLinkDto> Links);

/// <summary>A document of an item: linked from the item, or linked to the item from the document.</summary>
/// <param name="Id">The document.</param>
/// <param name="Title">The title.</param>
/// <param name="Kind">The document kind (for example <c>Receipt</c>, <c>Warranty</c>, <c>Manual</c>).</param>
/// <param name="ExpiresOn">The day the document expires, or null.</param>
public sealed record ItemDocumentDto(Guid Id, string Title, string Kind, DateOnly? ExpiresOn);

/// <summary>A task of an item (maintenance): linked from the item, or linked to the item from the task.</summary>
/// <param name="Id">The task.</param>
/// <param name="Title">The title.</param>
/// <param name="Status"><c>Open</c> or <c>Done</c>.</param>
/// <param name="DueOn">The next due date, or null.</param>
/// <param name="RepeatEvery">For a repeated task, how many units between two due dates, else null.</param>
/// <param name="RepeatUnit">For a repeated task, <c>Days</c>, <c>Weeks</c>, <c>Months</c>, or <c>Years</c>, else null.</param>
/// <param name="LastDoneAt">When the task was last done, or null.</param>
public sealed record ItemTaskDto(
    Guid Id,
    string Title,
    string Status,
    DateOnly? DueOn,
    int? RepeatEvery,
    string? RepeatUnit,
    DateTimeOffset? LastDoneAt);

/// <summary>One item, with its documents and its maintenance tasks.</summary>
/// <param name="Id">The item.</param>
/// <param name="Name">The name.</param>
/// <param name="RoomId">The room, or null.</param>
/// <param name="RoomName">The name of the room, or null.</param>
/// <param name="Category">Free text, or null.</param>
/// <param name="Brand">The brand, or null.</param>
/// <param name="Model">The model, or null.</param>
/// <param name="SerialNumber">The serial number, or null.</param>
/// <param name="Quantity">How many of this item the home has.</param>
/// <param name="PurchasedOn">The day of the purchase, or null.</param>
/// <param name="PurchasePrice">The price of one item, or null.</param>
/// <param name="CurrentValue">The value of one item now, or null.</param>
/// <param name="Value">The value of the row (see <see cref="ItemSummaryDto.Value"/>).</param>
/// <param name="WarrantyUntil">The last day of the warranty, or null.</param>
/// <param name="Notes">Free text, or null.</param>
/// <param name="Tags">The tags, in lower case.</param>
/// <param name="CreatedAt">When the item was added.</param>
/// <param name="UpdatedAt">When the item last changed.</param>
/// <param name="Links">The links of the item, oldest first.</param>
/// <param name="Documents">The documents of the item (from both sides of the link), by title.</param>
/// <param name="Maintenance">The tasks of the item (from both sides of the link), by due date.</param>
public sealed record ItemDto(
    Guid Id,
    string Name,
    Guid? RoomId,
    string? RoomName,
    string? Category,
    string? Brand,
    string? Model,
    string? SerialNumber,
    int Quantity,
    DateOnly? PurchasedOn,
    decimal? PurchasePrice,
    decimal? CurrentValue,
    decimal Value,
    DateOnly? WarrantyUntil,
    string? Notes,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ItemLinkDto> Links,
    IReadOnlyList<ItemDocumentDto> Documents,
    IReadOnlyList<ItemTaskDto> Maintenance);

/// <summary>A new item (<c>POST /api/home/items</c>). Only the name is required.</summary>
/// <param name="Name">The name (at most 200 characters).</param>
/// <param name="RoomId">The room, or null.</param>
/// <param name="Category">Free text, for example "Appliance", "Electronics", "Furniture".</param>
/// <param name="Brand">The brand.</param>
/// <param name="Model">The model.</param>
/// <param name="SerialNumber">The serial number.</param>
/// <param name="Quantity">How many (1 or more; default 1).</param>
/// <param name="PurchasedOn">The day of the purchase.</param>
/// <param name="PurchasePrice">The price of one item (0 or more, rounded to cents).</param>
/// <param name="CurrentValue">The value of one item now (0 or more, rounded to cents).</param>
/// <param name="WarrantyUntil">The last day of the warranty.</param>
/// <param name="Notes">Free text.</param>
/// <param name="Tags">The tags.</param>
/// <param name="Links">Links to make at once, for example to the receipt.</param>
public sealed record CreateItemRequest(
    string? Name,
    Guid? RoomId = null,
    string? Category = null,
    string? Brand = null,
    string? Model = null,
    string? SerialNumber = null,
    int? Quantity = null,
    DateOnly? PurchasedOn = null,
    decimal? PurchasePrice = null,
    decimal? CurrentValue = null,
    DateOnly? WarrantyUntil = null,
    string? Notes = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<CreateItemLinkRequest>? Links = null);

/// <summary>
/// The new details of an item (<c>PUT /api/home/items/{id}</c>). All values replace the old ones: a value that is not
/// sent becomes empty (the quantity becomes 1). The links do not change.
/// </summary>
public sealed record UpdateItemRequest(
    string? Name,
    Guid? RoomId = null,
    string? Category = null,
    string? Brand = null,
    string? Model = null,
    string? SerialNumber = null,
    int? Quantity = null,
    DateOnly? PurchasedOn = null,
    decimal? PurchasePrice = null,
    decimal? CurrentValue = null,
    DateOnly? WarrantyUntil = null,
    string? Notes = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>A new link (<c>POST /api/home/items/{id}/links</c>).</summary>
/// <param name="Kind">What to link to: <c>Documents.Document</c>, <c>Money.Transaction</c>, or <c>Tasks.Task</c>.</param>
/// <param name="TargetId">The id of the thing, for example the document id.</param>
public sealed record CreateItemLinkRequest(string? Kind, string? TargetId);

/// <summary>The items of one room in the summary.</summary>
/// <param name="RoomId">The room, or null for the items without a room.</param>
/// <param name="Name">The name of the room, or null for the items without a room.</param>
/// <param name="ItemCount">How many items (rows).</param>
/// <param name="TotalValue">The total value of these items.</param>
public sealed record RoomCountDto(Guid? RoomId, string? Name, int ItemCount, decimal TotalValue);

/// <summary>A category in use and how many items have it (<c>GET /api/home/categories</c>, T89).</summary>
public sealed record CategoryCountDto(string Name, int Count);

/// <summary>
/// One maintenance task of one item (<c>GET /api/home/maintenance</c>, T89): an open task linked to the item from either
/// side. A task linked to two items comes twice.
/// </summary>
/// <param name="TaskId">The task.</param>
/// <param name="Title">The title of the task.</param>
/// <param name="DueOn">The next due date, or null.</param>
/// <param name="RepeatEvery">For a repeated task, how many units between two due dates, else null.</param>
/// <param name="RepeatUnit">For a repeated task, <c>Days</c>, <c>Weeks</c>, <c>Months</c>, or <c>Years</c>, else null.</param>
/// <param name="LastDoneAt">When the task was last done, or null.</param>
/// <param name="ItemId">The item.</param>
/// <param name="ItemName">The name of the item.</param>
/// <param name="RoomName">The room of the item, or null.</param>
public sealed record MaintenanceDto(
    Guid TaskId,
    string Title,
    DateOnly? DueOn,
    int? RepeatEvery,
    string? RepeatUnit,
    DateTimeOffset? LastDoneAt,
    Guid ItemId,
    string ItemName,
    string? RoomName);

/// <summary>The numbers of the Home screen (<c>GET /api/home/summary</c>).</summary>
/// <param name="Date">Today, in the time zone of the module (<c>Home:TimeZone</c>).</param>
/// <param name="ItemCount">How many items (rows) the home has.</param>
/// <param name="TotalValue">The total value of all items.</param>
/// <param name="Rooms">
/// Each room in list order (also a room without items), then one entry for the items without a room (when there are any).
/// </param>
/// <param name="WarrantiesEnding">The items whose warranty ends today or in the next 30 days (<c>Home:WarrantySoonDays</c>).</param>
public sealed record HomeSummaryDto(DateOnly Date, int ItemCount, decimal TotalValue, IReadOnlyList<RoomCountDto> Rooms, int WarrantiesEnding);

/// <summary>The kinds of link that the Home module knows.</summary>
public static class ItemLinkKinds
{
    /// <summary>A document of the Documents module (a receipt, a manual, a warranty). The target id is the document id.</summary>
    public const string DocumentsDocument = "Documents.Document";

    /// <summary>A finance transaction (the purchase). The target id is the transaction id (a GUID).</summary>
    public const string MoneyTransaction = "Money.Transaction";

    /// <summary>A task of the Tasks module (maintenance). The target id is the task id.</summary>
    public const string TasksTask = "Tasks.Task";

    public static readonly IReadOnlyList<string> All = [DocumentsDocument, MoneyTransaction, TasksTask];
}
