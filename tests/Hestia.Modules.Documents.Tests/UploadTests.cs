using System.Net;
using System.Net.Http.Json;
using Hestia.Modules.Documents.Application;
using Hestia.Modules.Documents.Application.Ocr;
using Hestia.Modules.Documents.Contracts;
using Hestia.Testing;
using Microsoft.AspNetCore.Http;

namespace Hestia.Modules.Documents.Tests;

/// <summary>POST /api/documents: the files go to the storage folder once each, with the type from their content.</summary>
public class UploadTests
{
    private const string Documents = DocumentsHost.Documents;

    [Fact]
    public async Task WithoutSignIn_TheEndpointsAnswer401()
    {
        await using var host = DocumentsHost.Start(signedIn: false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(Documents)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.UploadAsync(DocumentsHost.Form("Kettle", files: [SampleFile.Png]))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync($"{Documents}/{Guid.NewGuid()}")).StatusCode);
        Assert.Empty(host.StoredFiles());
    }

    [Fact]
    public async Task Upload_ReturnsTheDocument_WithPendingOcr_AndStoresTheFiles()
    {
        await using var host = DocumentsHost.Start(new Dictionary<string, string?> { ["Documents:Ocr:Enabled"] = "false" });
        // The OCR is off, so the answer can be compared as it is.
        var response = await host.UploadAsync(DocumentsHost.Form("  Kettle receipt  ", "receipt", ["Kitchen", "  Small   appliances ", "kitchen"],
            "2026-09-01", "2028-09-01", " Bought at the corner shop ", SampleFile.Pdf, SampleFile.Png));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var document = await response.ReadAsync<DocumentDto>();
        Assert.Equal($"{Documents}/{document.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Kettle receipt", document.Title);
        Assert.Equal(DocumentKind.Receipt, document.Kind);
        Assert.Equal(new[] { "kitchen", "small appliances" }, document.Tags);
        Assert.Equal(((DateOnly?)new DateOnly(2026, 9, 1), (DateOnly?)new DateOnly(2028, 9, 1)), (document.IssuedOn, document.ExpiresOn));
        Assert.Equal("Bought at the corner shop", document.Notes);
        Assert.Equal(OcrStatus.NotNeeded, document.OcrStatus);
        Assert.Null(document.OcrText);
        Assert.Empty(document.Links);

        Assert.Equal(2, document.Files.Count);
        var (pdf, png) = (document.Files[0], document.Files[1]);
        Assert.Equal(("scanned-warranty.pdf", FileTypes.Pdf, (long)SampleFile.Pdf.Bytes.Length, SampleFile.Pdf.Sha256),
            (pdf.FileName, pdf.ContentType, pdf.Size, pdf.Sha256));
        Assert.Equal(("receipt.png", FileTypes.Png, SampleFile.Png.Sha256), (png.FileName, png.ContentType, png.Sha256));

        // Each file is at <storage>/<first two characters of its hash>/<hash>, with the uploaded bytes.
        Assert.Equal(new[] { Key(SampleFile.Pdf), Key(SampleFile.Png) }.Order(StringComparer.Ordinal), host.StoredFiles());
        Assert.Equal(SampleFile.Pdf.Bytes, await File.ReadAllBytesAsync(Path.Combine(host.StoragePath, Key(SampleFile.Pdf))));
    }

    [Fact]
    public async Task Upload_WithOcrOn_AnswersAtOnce_WithStatusPending()
    {
        await using var host = DocumentsHost.Start();
        var release = new ManualResetEventSlim();
        // An OCR that waits: the upload answers before the OCR ends.
        host.Ocr.Respond = (_, _) =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return OcrResult.Succeeded("Done later");
        };

        var document = await host.UploadOkAsync();

        Assert.Equal(OcrStatus.Pending, document.OcrStatus);
        release.Set();
        Assert.Equal(OcrStatus.Done, (await host.WaitForOcrAsync(document.Id)).OcrStatus);
    }

    [Fact]
    public async Task Upload_WithoutTitleOrKind_UsesTheFileName_AndOther()
    {
        await using var host = DocumentsHost.Start();

        var document = await host.UploadOkAsync(title: null, kind: null, files: [SampleFile.Png]);

        Assert.Equal(("receipt", DocumentKind.Other), (document.Title, document.Kind));
        Assert.Empty(document.Tags);
    }

    [Fact]
    public async Task TheSameContent_IsStoredOnce_AndStaysUntilItsLastDocumentIsDeleted()
    {
        await using var host = DocumentsHost.Start();
        var first = await host.UploadOkAsync("First", files: [SampleFile.Png]);
        var second = await host.UploadOkAsync("Second", files: [SampleFile.Png]);

        Assert.Equal(new[] { Key(SampleFile.Png) }, host.StoredFiles());
        Assert.Equal(first.Files[0].Sha256, second.Files[0].Sha256);

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{Documents}/{first.Id}")).StatusCode);
        Assert.Equal(new[] { Key(SampleFile.Png) }, host.StoredFiles());
        var download = await host.Client.GetAsync($"{Documents}/{second.Id}/files/{second.Files[0].Id}");
        Assert.Equal(SampleFile.Png.Bytes, await download.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{Documents}/{second.Id}")).StatusCode);
        Assert.Empty(host.StoredFiles());
    }

    [Fact]
    public async Task TheTypeComesFromTheContent_NotFromTheClient()
    {
        await using var host = DocumentsHost.Start();

        var document = await host.UploadOkAsync(files: [SampleFile.Png with { Name = "photo", ContentType = "application/pdf" }]);

        Assert.Equal((FileTypes.Png, "photo.png"), (document.Files[0].ContentType, document.Files[0].FileName));
    }

    [Fact]
    public async Task AFileOfAnotherType_IsABadRequest_AndNothingIsStored()
    {
        await using var host = DocumentsHost.Start();
        var text = new SampleFile("notes.pdf", "Just text, not a PDF"u8.ToArray(), FileTypes.Pdf);

        var response = await host.UploadAsync(DocumentsHost.Form("Notes", files: [SampleFile.Png, text]));

        await AssertBadRequestAsync(response, "files");
        Assert.Empty(host.StoredFiles());
        Assert.Equal(0, await host.CountAsync());
    }

    [Fact]
    public async Task AFileOverTheSizeLimit_IsABadRequest_AndNothingIsStored()
    {
        await using var host = DocumentsHost.Start(new Dictionary<string, string?> { ["Documents:MaxFileSizeMb"] = "1" });
        var large = new SampleFile("large.pdf", [.. "%PDF-1.7\n"u8, .. new byte[1024 * 1024]], FileTypes.Pdf);

        var response = await host.UploadAsync(DocumentsHost.Form("Large", files: [large]));

        var problem = await AssertBadRequestAsync(response, "files");
        Assert.Contains("1 MB", problem.Errors["files"][0]);
        Assert.Empty(host.StoredFiles());
    }

    [Fact]
    public async Task TooManyFiles_IsABadRequest()
    {
        await using var host = DocumentsHost.Start(new Dictionary<string, string?> { ["Documents:MaxFilesPerDocument"] = "2" });

        var response = await host.UploadAsync(DocumentsHost.Form("Three", files: [SampleFile.Png, SampleFile.Pdf, SampleFile.Webp]));

        await AssertBadRequestAsync(response, "files");
        Assert.Empty(host.StoredFiles());
    }

    [Fact]
    public async Task AnUploadWithoutFiles_OrNotAsAForm_IsABadRequest()
    {
        await using var host = DocumentsHost.Start();

        await AssertBadRequestAsync(await host.UploadAsync(DocumentsHost.Form("No files")), "files");
        await AssertBadRequestAsync(await host.Client.PostAsJsonAsync(Documents, new { title = "JSON" }), "files");
    }

    [Theory]
    [InlineData("kind", "Spaceship")]
    [InlineData("kind", "3")]
    [InlineData("expiresOn", "13/10/2026")]
    [InlineData("issuedOn", "2026-02-30")]
    public async Task ADetailThatCannotBeUsed_IsABadRequest_ForItsField(string field, string value)
    {
        await using var host = DocumentsHost.Start();
        var form = DocumentsHost.Form("Kettle", files: [SampleFile.Png]);
        form.Add(new StringContent(value), field);

        await AssertBadRequestAsync(await host.UploadAsync(form), field);
        Assert.Empty(host.StoredFiles());
    }

    [Fact]
    public async Task AnExpiryBeforeTheIssueDay_IsABadRequest()
    {
        await using var host = DocumentsHost.Start();

        var response = await host.UploadAsync(DocumentsHost.Form("Lease", "Lease", issuedOn: "2026-10-01", expiresOn: "2026-09-30",
            files: [SampleFile.Pdf]));

        await AssertBadRequestAsync(response, "expiresOn");
    }

    [Fact]
    public async Task ATagWithTheSeparator_OrTooManyTags_IsABadRequest()
    {
        await using var host = DocumentsHost.Start();

        await AssertBadRequestAsync(await host.UploadAsync(DocumentsHost.Form("Kettle", tags: ["a|b"], files: [SampleFile.Png])), "tags");
        var many = Enumerable.Range(1, 21).Select(i => $"tag{i}");
        await AssertBadRequestAsync(await host.UploadAsync(DocumentsHost.Form("Kettle", tags: [string.Join(",", many)], files: [SampleFile.Png])), "tags");
    }

    private static string Key(SampleFile file) => $"{file.Sha256[..2]}/{file.Sha256}";

    internal static async Task<HttpValidationProblemDetails> AssertBadRequestAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadAsync<HttpValidationProblemDetails>();
        Assert.True(problem.Errors.ContainsKey(field), $"No error for {field}: {string.Join(", ", problem.Errors.Keys)}");
        return problem;
    }
}
