using System.Net;
using System.Net.Http.Json;
using System.Text;
using Hestia.Modules.Home.Contracts;

namespace Hestia.Modules.Home.Tests;

/// <summary>
/// The CSV export for an insurance claim (T22): UTF-8 with a byte order mark, RFC 4180 quotes, one row per item with the
/// titles of its documents. The tests read the file back with a small CSV reader.
/// </summary>
public class ExportTests
{
    private const string Export = $"{HomeHost.Items}/export.csv";

    [Fact]
    public async Task TheExport_HasAllItems_InRoomOrder_AndReadsBackWithQuotesCommasAndAccents()
    {
        await using var host = HomeHost.Start();
        var kitchen = await host.CreateRoomAsync("Cuisine d'été");
        var office = await host.CreateRoomAsync("Office, upstairs");
        var receipt = await host.UploadDocumentAsync("Reçu \"Bosch\", 2025");
        var manual = await host.UploadDocumentAsync("Manual");
        await host.CreateAsync(new CreateItemRequest("Lave-vaisselle \"Série 4\", blanc", kitchen.Id, "Électroménager", "Bosch",
            "SMS4", "FD-99,12", 1, new DateOnly(2025, 3, 1), 899.99m, 750m, new DateOnly(2027, 3, 1),
            Links: [new CreateItemLinkRequest(ItemLinkKinds.DocumentsDocument, receipt.Id.ToString()),
                new CreateItemLinkRequest(ItemLinkKinds.DocumentsDocument, manual.Id.ToString())]));
        await host.CreateAsync(new CreateItemRequest("Chaise", office.Id, Quantity: 4, PurchasePrice: 75.5m));
        await host.CreateAsync(new CreateItemRequest("Bike"));
        await host.CreateAsync(new CreateItemRequest("Armoire", kitchen.Id, Notes: "Line one\nline two"));

        var response = await host.Client.GetAsync(Export);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal("home-inventory-2026-10-14.csv", response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        var rows = Csv.Read(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));

        Assert.Equal(new[]
        {
            new[] { "Room", "Name", "Category", "Brand", "Model", "Serial number", "Quantity", "Purchase date", "Purchase price", "Current value", "Warranty until", "Documents" },
            new[] { "Cuisine d'été", "Armoire", "", "", "", "", "1", "", "", "", "", "" },
            new[] { "Cuisine d'été", "Lave-vaisselle \"Série 4\", blanc", "Électroménager", "Bosch", "SMS4", "FD-99,12", "1", "2025-03-01",
                "899.99", "750.00", "2027-03-01", "Manual; Reçu \"Bosch\", 2025" },
            new[] { "Office, upstairs", "Chaise", "", "", "", "", "4", "", "75.50", "", "", "" },
            new[] { "", "Bike", "", "", "", "", "1", "", "", "", "", "" }
        }, rows);
    }

    [Fact]
    public async Task TheExport_UsesTheListFilters_AndProtectsAgainstFormulas()
    {
        await using var host = HomeHost.Start();
        var kitchen = await host.CreateRoomAsync("Kitchen");
        await host.CreateAsync(new CreateItemRequest("=HYPERLINK(\"http://example.com\")", kitchen.Id, Brand: "+Brand", Model: "-1", SerialNumber: "@sum"));
        await host.CreateAsync(new CreateItemRequest("Bike"));

        var rows = Csv.Read(Encoding.UTF8.GetString((await host.Client.GetByteArrayAsync($"{Export}?room={kitchen.Id}")).AsSpan(3)));

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "'=HYPERLINK(\"http://example.com\")", "'+Brand", "'-1", "'@sum" }, new[] { rows[1][1], rows[1][3], rows[1][4], rows[1][5] });
        await HomeHost.AssertBadRequestAsync(await host.Client.GetAsync($"{Export}?warranty=soon"), "warranty");
    }

    [Fact]
    public void TheQuote_FollowsRfc4180()
    {
        Assert.Equal("plain", Application.InventoryExport.Quote("plain"));
        Assert.Equal("\"a,b\"", Application.InventoryExport.Quote("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", Application.InventoryExport.Quote("say \"hi\""));
        Assert.Equal("\"two\nlines\"", Application.InventoryExport.Quote("two\nlines"));
    }

    /// <summary>A small RFC 4180 reader: quoted fields, two quotes for one, CRLF between records.</summary>
    private static class Csv
    {
        public static List<string[]> Read(string text)
        {
            var rows = new List<string[]>();
            var row = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else if (c == '"')
                    {
                        quoted = false;
                    }
                    else
                    {
                        field.Append(c);
                    }
                }
                else if (c == '"')
                {
                    quoted = true;
                }
                else if (c == ',')
                {
                    row.Add(field.ToString());
                    field.Clear();
                }
                else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add([.. row]);
                    row.Clear();
                    i++;
                }
                else
                {
                    field.Append(c);
                }
            }
            Assert.False(quoted, "A quoted field does not end.");
            Assert.True(field.Length == 0 && row.Count == 0, "The last record does not end with CRLF.");
            return rows;
        }
    }
}
