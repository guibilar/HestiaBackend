using System.Globalization;
using System.Text;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.Home.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.Home.Application;

/// <summary>A CSV file to send to the client.</summary>
public sealed record CsvFile(byte[] Content, string FileName);

/// <summary>
/// The inventory as a CSV file, for an insurance claim (<c>GET /api/home/items/export.csv</c>). One row per item, with
/// the titles of its documents. UTF-8 with a byte order mark, so a spreadsheet reads the accents correctly. Each field
/// follows RFC 4180: a field with a comma, a quote, or a line break is in quotes, and a quote in it is two quotes.
/// </summary>
public sealed class InventoryExport(ItemService items, ItemRelations relations, HomeDbContext db, HomeDays days)
{
    public const string ContentType = "text/csv; charset=utf-8";

    public static readonly IReadOnlyList<string> Header =
    [
        "Room", "Name", "Category", "Brand", "Model", "Serial number", "Quantity", "Purchase date", "Purchase price",
        "Current value", "Warranty until", "Documents"
    ];

    /// <summary>The items that match the filters (no paging): by room (in room order, items without a room last), then by name.</summary>
    public async Task<CsvFile> ExportAsync(ItemQuery query, CancellationToken cancellationToken)
    {
        var found = await items.ListAllAsync(query, cancellationToken);
        var roomOrder = await db.Rooms.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.SortOrder, cancellationToken);
        var names = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
        var ordered = found
            .OrderBy(i => i.RoomId is { } room && roomOrder.TryGetValue(room, out var order) ? order : int.MaxValue)
            .ThenBy(i => i.RoomName ?? string.Empty, names)
            .ThenBy(i => i.Name, names)
            .ThenBy(i => i.Id);

        var csv = new StringBuilder();
        AppendRow(csv, Header);
        foreach (var item in ordered)
        {
            var documents = await relations.DocumentsAsync(item.Id, item.Links, cancellationToken);
            AppendRow(csv,
            [
                Text(item.RoomName),
                Text(item.Name),
                Text(item.Category),
                Text(item.Brand),
                Text(item.Model),
                Text(item.SerialNumber),
                item.Quantity.ToString(CultureInfo.InvariantCulture),
                Day(item.PurchasedOn),
                Amount(item.PurchasePrice),
                Amount(item.CurrentValue),
                Day(item.WarrantyUntil),
                Text(string.Join("; ", documents.Select(d => d.Title)))
            ]);
        }

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var content = encoding.GetPreamble().Concat(encoding.GetBytes(csv.ToString())).ToArray();
        return new CsvFile(content, $"home-inventory-{days.Today:yyyy-MM-dd}.csv");
    }

    /// <summary>One row, with CRLF at the end (RFC 4180).</summary>
    public static void AppendRow(StringBuilder csv, IReadOnlyList<string> fields)
    {
        for (var index = 0; index < fields.Count; index++)
        {
            if (index > 0)
            {
                csv.Append(',');
            }
            csv.Append(Quote(fields[index]));
        }
        csv.Append("\r\n");
    }

    /// <summary>A field in quotes when it has a comma, a quote, or a line break; a quote in it becomes two quotes.</summary>
    public static string Quote(string field) =>
        field.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{field.Replace("\"", "\"\"")}\"" : field;

    /// <summary>
    /// A text of the user. A spreadsheet runs a cell that starts with "=", "+", "-", "@", a tab, or a carriage return as
    /// a formula, so such a text gets an apostrophe first (the usual protection against formula injection).
    /// </summary>
    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        return value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? $"'{value}" : value;
    }

    private static string Day(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Amount(decimal? amount) => amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty;
}
