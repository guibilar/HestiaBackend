using System.Text.RegularExpressions;
using Hestia.Platform.Exceptions;

namespace Hestia.Modules.Home.Domain;

/// <summary>
/// The tags of an item, as in the Documents and Tasks modules: one text column <c>|tag1|tag2|</c>, so a filter by tag is
/// a text search for <c>|tag|</c>, and the full-text index can read them.
/// </summary>
public static partial class ItemTags
{
    public const int MaxTags = 20;
    public const int MaxTagLength = 50;
    public const char Separator = '|';

    /// <summary>The longest stored value: 20 tags of 50 characters, each after a separator, and the last separator.</summary>
    public const int MaxStoredLength = MaxTags * (MaxTagLength + 1) + 1;

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    /// <summary>One line of text: trimmed, with single spaces.</summary>
    public static string OneLine(string value) => Spaces().Replace(value.Trim(), " ");

    /// <summary>
    /// The tags as the user gave them, cleaned: each value can hold several tags with commas between them. A tag is
    /// trimmed, in lower case, with single spaces. Empty tags and repeats are left out.
    /// </summary>
    /// <exception cref="InvalidInputException">A tag is too long or has a "|", or there are too many tags.</exception>
    public static List<string> Normalize(IEnumerable<string?>? values)
    {
        var tags = new List<string>();
        foreach (var value in values ?? [])
        {
            foreach (var part in (value ?? string.Empty).Split(','))
            {
                var tag = OneLine(part).ToLowerInvariant();
                if (tag.Length == 0 || tags.Contains(tag))
                {
                    continue;
                }
                if (tag.Contains(Separator))
                {
                    throw new InvalidInputException("tags", $"A tag cannot have the character \"{Separator}\".");
                }
                if (tag.Length > MaxTagLength)
                {
                    throw new InvalidInputException("tags", $"A tag can have at most {MaxTagLength} characters.");
                }
                tags.Add(tag);
            }
        }
        if (tags.Count > MaxTags)
        {
            throw new InvalidInputException("tags", $"An item can have at most {MaxTags} tags.");
        }
        return tags;
    }

    public static string Store(IReadOnlyCollection<string> tags) =>
        tags.Count == 0 ? string.Empty : $"{Separator}{string.Join(Separator, tags)}{Separator}";

    public static List<string> Read(string stored) =>
        stored.Split(Separator, StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>The text to search for in the stored value, to find one tag.</summary>
    public static string Pattern(string tag) => $"{Separator}{tag}{Separator}";
}

/// <summary>The details of an item that the user sets: checked and cleaned for a create and for a change.</summary>
public sealed record ItemDetails(
    string Name,
    Guid? RoomId,
    string? Category,
    string? Brand,
    string? Model,
    string? SerialNumber,
    int Quantity,
    DateOnly? PurchasedOn,
    decimal? PurchasePrice,
    decimal? CurrentValue,
    DateOnly? WarrantyUntil,
    string? Notes,
    List<string> Tags)
{
    /// <summary>The largest amount that the amount columns can keep (numeric(12,2)).</summary>
    public const decimal MaxAmount = 9_999_999_999.99m;

    /// <exception cref="InvalidInputException">A value cannot be used.</exception>
    public static ItemDetails Create(string? name, Guid? roomId, string? category, string? brand, string? model,
        string? serialNumber, int? quantity, DateOnly? purchasedOn, decimal? purchasePrice, decimal? currentValue,
        DateOnly? warrantyUntil, string? notes, IEnumerable<string?>? tags)
    {
        var cleanName = Text(name, "name", Item.MaxNameLength) ?? throw new InvalidInputException("name", "Enter a name.");
        var count = quantity ?? 1;
        if (count is < 1 or > Item.MaxQuantity)
        {
            throw new InvalidInputException("quantity", $"The quantity must be from 1 to {Item.MaxQuantity}.");
        }
        var cleanNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (cleanNotes?.Length > Item.MaxNotesLength)
        {
            throw new InvalidInputException("notes", $"The notes can have at most {Item.MaxNotesLength} characters.");
        }
        return new ItemDetails(
            cleanName,
            roomId == Guid.Empty ? null : roomId,
            Text(category, "category", Item.MaxCategoryLength),
            Text(brand, "brand", Item.MaxBrandLength),
            Text(model, "model", Item.MaxModelLength),
            Text(serialNumber, "serialNumber", Item.MaxSerialNumberLength),
            count,
            purchasedOn,
            Amount(purchasePrice, "purchasePrice"),
            Amount(currentValue, "currentValue"),
            warrantyUntil,
            cleanNotes,
            ItemTags.Normalize(tags));
    }

    /// <summary>One line of text (trimmed, single spaces), or null for an empty value.</summary>
    /// <exception cref="InvalidInputException">The text is too long.</exception>
    public static string? Text(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var text = ItemTags.OneLine(value);
        if (text.Length > maxLength)
        {
            throw new InvalidInputException(field, $"The {field} can have at most {maxLength} characters.");
        }
        return text;
    }

    /// <summary>An amount, rounded to cents (half away from zero).</summary>
    /// <exception cref="InvalidInputException">The amount is less than 0 or too large.</exception>
    public static decimal? Amount(decimal? value, string field)
    {
        if (value is not { } amount)
        {
            return null;
        }
        if (amount < 0 || amount > MaxAmount)
        {
            throw new InvalidInputException(field, $"The amount must be from 0 to {MaxAmount:0.00}.");
        }
        return Math.Round(amount, Item.AmountScale, MidpointRounding.AwayFromZero);
    }

    public void ApplyTo(Item item)
    {
        item.Name = Name;
        item.RoomId = RoomId;
        item.Category = Category;
        item.Brand = Brand;
        item.Model = Model;
        item.SerialNumber = SerialNumber;
        item.Quantity = Quantity;
        item.PurchasedOn = PurchasedOn;
        item.PurchasePrice = PurchasePrice;
        item.CurrentValue = CurrentValue;
        item.WarrantyUntil = WarrantyUntil;
        item.Notes = Notes;
        item.Tags = ItemTags.Store(Tags);
    }
}
