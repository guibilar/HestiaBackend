using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hestia.Modules.Documents.Application.Ocr;
using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Documents.Infrastructure;
using Hestia.Platform.Paging;
using Hestia.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Documents.Tests;

/// <summary>Get, change, delete, download, and list documents.</summary>
public class DocumentApiTests
{
    private const string Documents = DocumentsHost.Documents;

    [Fact]
    public async Task Get_ReturnsTheDocument_And404ForAnUnknownId()
    {
        await using var host = DocumentsHost.Start();
        var created = await host.UploadOkAsync();

        var found = await (await host.Client.GetAsync($"{Documents}/{created.Id}")).ReadAsync<DocumentDto>();
        var missing = await host.Client.GetAsync($"{Documents}/{Guid.NewGuid()}");

        Assert.Equal(created.Id, found.Id);
        Assert.Equal(created.Files.Select(f => f.Id), found.Files.Select(f => f.Id));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Update_ReplacesTheDetails_KeepsTheFiles_AndRaisesDocumentChanged()
    {
        await using var host = DocumentsHost.Start();
        var created = await host.UploadOkAsync(tags: ["old"], notes: "Old notes");

        var response = await host.Client.PutAsJsonAsync($"{Documents}/{created.Id}", new UpdateDocumentRequest(
            "Toaster warranty", DocumentKind.Warranty, ["Kitchen"], new DateOnly(2026, 1, 2), new DateOnly(2028, 1, 2), null), HestiaApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.ReadAsync<DocumentDto>();
        Assert.Equal(("Toaster warranty", DocumentKind.Warranty, (DateOnly?)new DateOnly(2028, 1, 2), (string?)null),
            (updated.Title, updated.Kind, updated.ExpiresOn, updated.Notes));
        Assert.Equal(new[] { "kitchen" }, updated.Tags);
        Assert.Equal(created.Files.Select(f => f.Id), updated.Files.Select(f => f.Id));
        Assert.True(updated.UpdatedAt >= created.UpdatedAt);

        var changed = await host.Factory.Events.WaitForAsync<DocumentChanged>(e => e.Change == DocumentChange.Details);
        Assert.Equal((created.Id, "Toaster warranty", DocumentKind.Warranty), (changed.DocumentId, changed.Title, changed.Kind));
        Assert.Equal(new[] { "kitchen" }, changed.Tags);
    }

    [Fact]
    public async Task Update_WithoutATitle_IsABadRequest_And404ForAnUnknownId()
    {
        await using var host = DocumentsHost.Start();
        var created = await host.UploadOkAsync();

        await UploadTests.AssertBadRequestAsync(await host.Client.PutAsJsonAsync($"{Documents}/{created.Id}",
            new UpdateDocumentRequest(" ", DocumentKind.Other, null, null, null, null), HestiaApiFactory.Json), "title");
        await UploadTests.AssertBadRequestAsync(await host.Client.PutAsJsonAsync($"{Documents}/{created.Id}",
            new UpdateDocumentRequest("Kettle", null, null, null, null, null), HestiaApiFactory.Json), "kind");
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PutAsJsonAsync($"{Documents}/{Guid.NewGuid()}",
            new UpdateDocumentRequest("Kettle", DocumentKind.Other, null, null, null, null), HestiaApiFactory.Json)).StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesTheDocument_AndItsFile_AndRaisesDocumentDeleted()
    {
        await using var host = DocumentsHost.Start();
        var created = await host.UploadOkAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{Documents}/{created.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"{Documents}/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync($"{Documents}/{created.Id}")).StatusCode);
        Assert.Empty(host.StoredFiles());
        var deleted = await host.Factory.Events.WaitForAsync<DocumentDeleted>();
        Assert.Equal((created.Id, created.Title), (deleted.DocumentId, deleted.Title));
    }

    [Fact]
    public async Task Download_SendsTheFile_AsAnAttachment_OrInline()
    {
        await using var host = DocumentsHost.Start();
        var created = await host.UploadOkAsync(files: [SampleFile.Pdf]);
        var file = created.Files[0];
        var path = $"{Documents}/{created.Id}/files/{file.Id}";

        var download = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(SampleFile.Pdf.Bytes, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("scanned-warranty.pdf", download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("nosniff", string.Join(",", download.Headers.GetValues("X-Content-Type-Options")));

        var inline = await host.Client.GetAsync($"{path}?inline=true");
        Assert.Equal("inline", inline.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(SampleFile.Pdf.Bytes, await inline.Content.ReadAsByteArrayAsync());

        // A file of another document, or an unknown file: 404.
        var other = await host.UploadOkAsync("Other");
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"{Documents}/{other.Id}/files/{file.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"{Documents}/{created.Id}/files/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task AReadOnlyUser_CanRead_ButChangesNothing()
    {
        await using var host = DocumentsHost.Start();
        var created = await host.UploadOkAsync();
        var readOnly = host.Factory.CreateClient();
        readOnly.DefaultRequestHeaders.Add(TestSignIn.ReadOnlyHeader, "yes");

        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync(Documents)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"{Documents}/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"{Documents}/{created.Id}/files/{created.Files[0].Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"{Documents}/by-link?kind=Money.Transaction&id={Guid.NewGuid()}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.UploadAsync(DocumentsHost.Form("New", files: [SampleFile.Pdf]), readOnly)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PutAsJsonAsync($"{Documents}/{created.Id}",
            new UpdateDocumentRequest("Changed", DocumentKind.Other, null, null, null, null), HestiaApiFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.DeleteAsync($"{Documents}/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsync($"{Documents}/{created.Id}/ocr", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync($"{Documents}/{created.Id}/links",
            new CreateDocumentLinkRequest(DocumentLinkKinds.MoneyTransaction, Guid.NewGuid().ToString()))).StatusCode);

        Assert.Equal(1, await host.CountAsync());
        Assert.Equal("Kettle receipt", (await (await host.Client.GetAsync($"{Documents}/{created.Id}")).ReadAsync<DocumentDto>()).Title);
    }

    [Fact]
    public async Task List_FiltersByKindTagAndExpiry_NewestFirst_InPages()
    {
        await using var host = DocumentsHost.Start();
        var lease = await host.UploadOkAsync("Lease", "Lease", ["home"], expiresOn: "2027-06-30");
        var warranty = await host.UploadOkAsync("Toaster warranty", "Warranty", ["kitchen", "home"], expiresOn: "2026-12-31");
        var receipt = await host.UploadOkAsync("Kettle receipt", "Receipt", ["kitchen"]);

        Assert.Equal(new[] { receipt.Id, warranty.Id, lease.Id }, (await ListAsync(host, "")).Items.Select(d => d.Id));
        Assert.Equal(new[] { warranty.Id }, (await ListAsync(host, "kind=warranty")).Items.Select(d => d.Id));
        Assert.Equal(new[] { warranty.Id, lease.Id }, (await ListAsync(host, "tag=Home")).Items.Select(d => d.Id));
        Assert.Equal(new[] { warranty.Id }, (await ListAsync(host, "expiresFrom=2026-12-31&expiresTo=2027-06-29")).Items.Select(d => d.Id));
        Assert.Equal(new[] { lease.Id }, (await ListAsync(host, "expiresFrom=2027-01-01")).Items.Select(d => d.Id));

        var page = await ListAsync(host, "page=2&pageSize=2");
        Assert.Equal((3, 2), (page.TotalCount, page.TotalPages));
        Assert.Equal(new[] { lease.Id }, page.Items.Select(d => d.Id));
        Assert.Equal(new[] { "home" }, page.Items[0].Tags);
        Assert.Single(page.Items[0].Files);
    }

    [Fact]
    public async Task ExpiringSoon_ListsTheDocumentsThatExpire_FromTheServersToday_ToTheSoonDays()
    {
        await using var host = DocumentsHost.Start(new Dictionary<string, string?>
        {
            ["Documents:TimeZone"] = "UTC",
            ["Documents:Expiry:SoonDays"] = "30",
        });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var soon = await host.UploadOkAsync("Passport", "Other", expiresOn: today.AddDays(10).ToString("yyyy-MM-dd"));
        var lastDay = await host.UploadOkAsync("Insurance", "Insurance", expiresOn: today.AddDays(30).ToString("yyyy-MM-dd"));
        await host.UploadOkAsync("Lease", "Lease", expiresOn: today.AddDays(31).ToString("yyyy-MM-dd"));
        await host.UploadOkAsync("Old warranty", "Warranty", expiresOn: today.AddDays(-1).ToString("yyyy-MM-dd"));
        await host.UploadOkAsync("Receipt", "Receipt");

        var found = await ListAsync(host, "expiring=Soon");

        Assert.Equal(new[] { lastDay.Id, soon.Id }, found.Items.Select(d => d.Id));
    }

    [Theory]
    [InlineData("kind=Spaceship", "kind")]
    [InlineData("expiresFrom=tomorrow", "expiresFrom")]
    [InlineData("expiresTo=2026-13-01", "expiresTo")]
    [InlineData("expiring=later", "expiring")]
    [InlineData("pageSize=101", "pageSize")]
    public async Task List_WithAFilterThatCannotBeUsed_IsABadRequest(string query, string field)
    {
        await using var host = DocumentsHost.Start();

        await UploadTests.AssertBadRequestAsync(await host.Client.GetAsync($"{Documents}?{query}"), field);
    }

    [Fact]
    public async Task Search_FindsWordsInTheTitle_TheTags_TheNotes_AndTheOcrText()
    {
        await using var host = DocumentsHost.Start();
        host.Ocr.Respond = (_, _) => OcrResult.Succeeded("Manufacturer: Breville. Repairs are free for two years.");
        var toaster = await host.UploadOkAsync("Toaster warranty", "Warranty", ["kitchen"], notes: "Keep the box");
        var kettle = await host.UploadOkAsync("Kettle receipt", "Receipt");
        await host.WaitForOcrAsync(toaster.Id);
        await host.WaitForOcrAsync(kettle.Id);

        Assert.Equal(new[] { toaster.Id }, (await ListAsync(host, "q=toaster")).Items.Select(d => d.Id));
        Assert.Equal(new[] { toaster.Id }, (await ListAsync(host, "q=box")).Items.Select(d => d.Id));
        Assert.Equal(new[] { toaster.Id }, (await ListAsync(host, "q=kitchen")).Items.Select(d => d.Id));
        Assert.Equal(new[] { kettle.Id }, (await ListAsync(host, "q=kettle")).Items.Select(d => d.Id));
        // Both have the OCR text.
        Assert.Equal(2, (await ListAsync(host, "q=breville")).TotalCount);
        Assert.Empty((await ListAsync(host, "q=lawnmower")).Items);
        // With a filter.
        Assert.Equal(new[] { kettle.Id }, (await ListAsync(host, "q=breville&kind=Receipt")).Items.Select(d => d.Id));
    }

    [Fact]
    public async Task Search_InPostgres_UsesTheEnglishStems_AndRanksTheTitleFirst()
    {
        Assert.SkipUnless(TestDatabase.OnPostgres, $"Needs PostgreSQL: set {TestDatabase.Variable}.");
        await using var host = DocumentsHost.Start();
        host.Ocr.Respond = (_, _) => OcrResult.Succeeded("This card is the warranty of the appliance.");
        var withText = await host.UploadOkAsync("Appliance card", "Other");
        var withTitle = await host.UploadOkAsync("Fridge warranties", "Warranty");
        await host.WaitForOcrAsync(withText.Id);
        await host.WaitForOcrAsync(withTitle.Id);

        // "warranty" and "warranties" have the same English stem. A match in the title weighs more than in the OCR text.
        Assert.Equal(new[] { withTitle.Id, withText.Id }, (await ListAsync(host, "q=warranty")).Items.Select(d => d.Id));
        // Web search syntax: "-fridge" leaves out the documents with the word.
        Assert.Equal(new[] { withText.Id }, (await ListAsync(host, "q=warranty -fridge")).Items.Select(d => d.Id));
    }

    [Fact]
    public async Task TheOpenApiDocument_DescribesTheDocumentsEndpoints()
    {
        await using var host = DocumentsHost.Start();

        var response = await host.Client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.Equal("UploadDocument", paths.GetProperty(Documents).GetProperty("post").GetProperty("operationId").GetString());
        Assert.Equal("ListDocuments", paths.GetProperty(Documents).GetProperty("get").GetProperty("operationId").GetString());
        Assert.True(paths.TryGetProperty($"{Documents}/{{id}}/files/{{fileId}}", out _));
        Assert.True(paths.TryGetProperty($"{Documents}/by-link", out _));
        Assert.True(document.RootElement.GetProperty("components").GetProperty("schemas").TryGetProperty("DocumentDto", out _));
    }

    [Fact]
    public async Task Migrations_PutTheTablesAndTheirHistory_InTheDocumentsSchema()
    {
        Assert.SkipUnless(TestDatabase.OnPostgres, $"Needs PostgreSQL: set {TestDatabase.Variable}.");
        await using var host = DocumentsHost.Start();
        host.Factory.CreateClient();

        var tables = await host.Factory.WithScopeAsync(services =>
            services.GetRequiredService<DocumentsDbContext>().Database
                .SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'documents'")
                .ToListAsync());

        Assert.Equal(new[] { "DocumentFiles", "DocumentLinks", "Documents", "EventInbox", "EventOutbox", "__EFMigrationsHistory" },
            tables.Order(StringComparer.Ordinal));
    }

    private static async Task<PaginatedList<DocumentSummaryDto>> ListAsync(DocumentsHost host, string query)
    {
        var response = await host.Client.GetAsync($"{Documents}?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<PaginatedList<DocumentSummaryDto>>();
    }
}
