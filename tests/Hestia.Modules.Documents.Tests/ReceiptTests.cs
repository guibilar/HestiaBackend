using System.Net;
using System.Text.Json;
using Hestia.Modules.Documents.Application.Ocr;
using Hestia.Modules.Documents.Application.Receipts;
using Hestia.Modules.Documents.Contracts;
using Hestia.Testing;
using Microsoft.AspNetCore.Http;

namespace Hestia.Modules.Documents.Tests;

/// <summary>T100: what the text of a receipt says, and the finance transactions that can be its purchase.</summary>
public class ReceiptTests
{
    private static readonly DateOnly Today = new(2026, 10, 16);

    private const string Costco = """
        COSTCO WHOLESALE
        Calgary #1234
        11588 Sarcee Trail NW
        Member 111122223333
        E 1234567 KS PAPER TOWEL      24.99
        E 7654321 MILK 2% 4L           6.49
        SUBTOTAL                      31.48
        GST                            1.57
        TOTAL                         33.05
        XXXXXXXXXXXX1234 CHIP VISA
        APPROVED - THANK YOU
        10/03/2026 14:22 1234 56 789
        TOTAL NUMBER OF ITEMS SOLD = 2
        """;

    private const string HomeDepot = """
        The Home Depot
        Store #7031 403-555-0100
        RYOBI 18V DRILL KIT          129.00
        Protection Plan 2 Year Warranty
        SUB-TOTAL                    129.00
        GST 5%                         6.45
        TOTAL                       $135.45
        Visa
        Oct 1, 2026
        """;

    // OCR often puts the amount of the total on its own line.
    private const string Split = """
        Corner Bakery
        2026-10-12
        Croissant 3.50
        Total
        $ 1,203.75
        Change 0.00
        """;

    [Fact]
    public void ACostcoReceipt_GivesTheStore_TheDay_AndTheTotal_NotTheSubtotal()
    {
        var facts = ReceiptReader.Read(Costco, Today);

        Assert.Equal(("Costco Wholesale", new DateOnly(2026, 10, 3), 33.05m), (facts.Merchant, facts.Date, facts.Total));
        Assert.Null(facts.WarrantyMonths);
    }

    [Fact]
    public void AReceiptWithAWarranty_GivesItsLength_AndAWrittenDate()
    {
        var facts = ReceiptReader.Read(HomeDepot, Today);

        Assert.Equal(("The Home Depot", new DateOnly(2026, 10, 1), 135.45m, 24), (facts.Merchant, facts.Date, facts.Total, facts.WarrantyMonths));
    }

    [Fact]
    public void ATotalOnTheNextLine_AndThousands_AreRead()
    {
        var facts = ReceiptReader.Read(Split, Today);

        Assert.Equal(("Corner Bakery", new DateOnly(2026, 10, 12), 1203.75m), (facts.Merchant, facts.Date, facts.Total));
    }

    [Theory]
    [InlineData("Date: 2026-10-03", 2026, 10, 3)]
    [InlineData("10/03/2026", 2026, 10, 3)]
    [InlineData("25/09/2026", 2026, 9, 25)]
    [InlineData("10/03/26", 2026, 10, 3)]
    [InlineData("3 Oct 2026", 2026, 10, 3)]
    [InlineData("October 3rd, 2026", 2026, 10, 3)]
    [InlineData("Sept. 30 2026", 2026, 9, 30)]
    public void Dates_InTheCommonForms(string line, int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), ReceiptReader.Date([line], Today));

    [Fact]
    public void ADateInTheFuture_OrBefore2000_IsNotThePurchaseDay()
    {
        Assert.Null(ReceiptReader.Date(["Return by 2027-01-15"], Today));
        Assert.Equal(new DateOnly(2026, 10, 3), ReceiptReader.Date(["Return by 2027-01-15", "Sold 2026-10-03"], Today));
        Assert.Null(ReceiptReader.Date(["Since 1999-01-01"], Today));
    }

    [Theory]
    [InlineData("1 year limited warranty", 12)]
    [InlineData("Warranty: 3 years parts and labour", 36)]
    [InlineData("two-year warranty", 24)]
    [InlineData("90 days return", null)]
    public void WarrantyLengths(string text, int? months) =>
        Assert.Equal(months, ReceiptReader.Read($"Shop\n{text}", Today).WarrantyMonths);

    [Fact]
    public void NoText_GivesNothing() =>
        Assert.Equal(new ReceiptFacts(null, null, null, null, null), ReceiptReader.Read("  ", Today));

    /// <summary>Finance's transaction filter: it answers the transactions of a page, whatever the filter.</summary>
    private static Func<HttpContext, Task> Finance(params object[] items) => async context =>
    {
        if (context.Request.Path == "/health/ready")
        {
            await context.Response.WriteAsync("Healthy");
            return;
        }
        await context.Response.WriteAsJsonAsync(new { items, page = 1, pageSize = 100, totalCount = items.Length });
    };

    [Fact]
    public async Task TheSuggestions_OfAReceipt_HaveTheTransactionsOfTheSameAmount_ClosestFirst()
    {
        var match = Guid.NewGuid();
        var later = Guid.NewGuid();
        await using var finance = await FakeFinance.StartAsync(Finance(
            new { id = Guid.NewGuid(), date = "2026-10-03T00:00:00", description = "SAFEWAY", amount = 33.10m, accountName = "Visa" },
            new { id = later, date = "2026-10-05T00:00:00", description = "COSTCO WHOLESALE #1234", amount = 33.05m, accountName = "Visa" },
            new { id = match, date = "2026-10-04T00:00:00", description = "COSTCO WHOLESALE", amount = 33.05m, accountName = "Visa" }));
        await using var host = DocumentsHost.Start(finance.HostSettings());
        host.Ocr.Respond = (_, _) => OcrResult.Succeeded(Costco);

        var document = await host.UploadOkAsync(title: "Scan", kind: "Other");
        await host.WaitForOcrAsync(document.Id);
        var response = await host.Client.GetAsync($"{DocumentsHost.Documents}/{document.Id}/suggestions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var suggestions = await response.ReadAsync<DocumentSuggestionsDto>();
        Assert.Equal(("Costco Wholesale", new DateOnly(2026, 10, 3), 33.05m, DocumentKind.Receipt),
            (suggestions.Merchant, suggestions.Date, suggestions.Total, suggestions.Kind));
        Assert.True(suggestions.TransactionsComplete);
        Assert.Equal([(match, 1), (later, 2)], suggestions.Transactions.Select(t => (t.Id, t.DaysOff)));
        Assert.All(suggestions.Transactions, t => Assert.False(t.Linked));

        // Finance gets the days around the receipt.
        var filter = JsonDocument.Parse(finance.Requests.Last(r => r.Target == "/api/Transaction/Filter").Body).RootElement;
        Assert.Equal(("2026-09-30T00:00:00", "2026-10-06T00:00:00"),
            (filter.GetProperty("startDate").GetString(), filter.GetProperty("endDate").GetString()));
    }

    [Fact]
    public async Task AWarranty_EndsItsLengthAfterThePurchase_AndFinanceDown_IsNotAnError()
    {
        await using var finance = await FakeFinance.StartAsync(async context =>
        {
            context.Response.StatusCode = context.Request.Path == "/health/ready" ? 200 : 503;
            await Task.CompletedTask;
        });
        await using var host = DocumentsHost.Start(finance.HostSettings());
        host.Ocr.Respond = (_, _) => OcrResult.Succeeded(HomeDepot);

        var document = await host.UploadOkAsync(title: "Drill", kind: "Receipt");
        await host.WaitForOcrAsync(document.Id);
        var suggestions = await (await host.Client.GetAsync($"{DocumentsHost.Documents}/{document.Id}/suggestions")).ReadAsync<DocumentSuggestionsDto>();

        Assert.Equal((24, new DateOnly(2028, 10, 1)), (suggestions.WarrantyMonths!.Value, suggestions.WarrantyUntil!.Value));
        // The kind is Receipt already: no kind to suggest.
        Assert.Null(suggestions.Kind);
        Assert.False(suggestions.TransactionsComplete);
        Assert.Empty(suggestions.Transactions);
    }

    [Fact]
    public async Task BeforeTheOcr_ThereAreNoSuggestions_AndAnUnknownDocumentIs404()
    {
        await using var host = DocumentsHost.Start(new Dictionary<string, string?> { ["Documents:Ocr:Enabled"] = "false" });

        var document = await host.UploadOkAsync();
        var suggestions = await (await host.Client.GetAsync($"{DocumentsHost.Documents}/{document.Id}/suggestions")).ReadAsync<DocumentSuggestionsDto>();

        Assert.Equal((OcrStatus.NotNeeded, (decimal?)null), (suggestions.OcrStatus, suggestions.Total));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"{DocumentsHost.Documents}/{Guid.NewGuid()}/suggestions")).StatusCode);
    }
}
