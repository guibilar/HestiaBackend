using Hestia.Platform.Domain;
using NpgsqlTypes;

namespace Hestia.Modules.Home.Domain;

/// <summary>A room of the home. The user adds the rooms; the module starts with none.</summary>
public class Room : BaseEntity
{
    public const int MaxNameLength = 100;

    public string Name { get; set; } = string.Empty;

    /// <summary>The place of the room in the list: lower first.</summary>
    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A thing in the home: where it is, what it cost, its serial number, and its warranty.</summary>
public class Item : BaseEntity
{
    public const int MaxNameLength = 200;
    public const int MaxCategoryLength = 50;
    public const int MaxBrandLength = 100;
    public const int MaxModelLength = 100;
    public const int MaxSerialNumberLength = 100;
    public const int MaxNotesLength = 4000;
    public const int MaxQuantity = 100_000;

    /// <summary>The digits of an amount column: up to 9 999 999 999.99.</summary>
    public const int AmountPrecision = 12;

    public const int AmountScale = 2;

    public string Name { get; set; } = string.Empty;

    /// <summary>The room, or null. A room with items cannot be deleted.</summary>
    public Guid? RoomId { get; set; }

    public string? Category { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public int Quantity { get; set; } = 1;
    public DateOnly? PurchasedOn { get; set; }

    /// <summary>The price of one item, in the one app currency (no currency field in v1).</summary>
    public decimal? PurchasePrice { get; set; }

    /// <summary>The value of one item now. When it is set, the totals and the export use it, else the purchase price.</summary>
    public decimal? CurrentValue { get; set; }

    public DateOnly? WarrantyUntil { get; set; }
    public string? Notes { get; set; }

    /// <summary>The tags, stored as <c>|tag1|tag2|</c> (see <see cref="ItemTags"/>), or empty.</summary>
    public string Tags { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// The full-text index of the name, the brand, the model, the serial number, the category, the notes, and the tags:
    /// a generated column (PostgreSQL only). The database computes it; the code never sets it.
    /// </summary>
    public NpgsqlTsVector? SearchVector { get; set; }

    public List<ItemLink> Links { get; set; } = [];

    /// <summary>The value of the row: the current value (else the purchase price, else 0) times the quantity.</summary>
    public decimal Value => (CurrentValue ?? PurchasePrice ?? 0m) * Quantity;
}

/// <summary>
/// A link from an item to something outside the module. The kind and the id are text, as in the Documents and Tasks
/// modules.
/// </summary>
public class ItemLink : BaseEntity
{
    public const int MaxKindLength = 50;
    public const int MaxTargetIdLength = 100;

    public Guid ItemId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
