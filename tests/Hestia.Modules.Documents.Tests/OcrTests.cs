using System.Net;
using Hestia.Modules.Documents.Application;
using Hestia.Modules.Documents.Application.Ocr;
using Hestia.Modules.Documents.Contracts;
using Hestia.Platform.Paging;
using Hestia.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Documents.Tests;

/// <summary>The background OCR: status, text, failures, and the retry.</summary>
public class OcrTests
{
    private const string Documents = DocumentsHost.Documents;

    [Fact]
    public async Task TheOcr_RunsInTheBackground_StoresTheTextOfAllFiles_AndRaisesDocumentChanged()
    {
        await using var host = DocumentsHost.Start();
        host.Ocr.Respond = (_, contentType) => OcrResult.Succeeded(contentType == FileTypes.Pdf ? "WARRANTY CARD" : "KETTLE 49.99");

        var created = await host.UploadOkAsync(files: [SampleFile.Pdf, SampleFile.Png]);
        var document = await host.WaitForOcrAsync(created.Id);

        Assert.Equal(OcrStatus.Done, document.OcrStatus);
        Assert.Equal("WARRANTY CARD\n\nKETTLE 49.99", document.OcrText);
        Assert.Null(document.OcrError);
        var changed = await host.Factory.Events.WaitForAsync<DocumentChanged>(e => e.Change == DocumentChange.OcrText);
        Assert.Equal((created.Id, OcrStatus.Done), (changed.DocumentId, changed.OcrStatus));
        // The engine read the stored files.
        Assert.All(host.Ocr.Calls, path => Assert.StartsWith(host.StoragePath, path));
        Assert.Equal(2, host.Ocr.Calls.Count);
    }

    [Fact]
    public async Task AFailedOcr_KeepsTheDocument_WithTheErrorOfEachFile_AndTheTextThatWasFound()
    {
        await using var host = DocumentsHost.Start();
        host.Ocr.Respond = (_, contentType) => contentType == FileTypes.Pdf
            ? OcrResult.Failed("The PDF is encrypted.", "Text layer of the PDF")
            : OcrResult.Succeeded("KETTLE 49.99");

        var created = await host.UploadOkAsync(files: [SampleFile.Pdf, SampleFile.Png]);
        var document = await host.WaitForOcrAsync(created.Id);

        Assert.Equal(OcrStatus.Failed, document.OcrStatus);
        Assert.Equal("scanned-warranty.pdf: The PDF is encrypted.", document.OcrError);
        Assert.Equal("Text layer of the PDF\n\nKETTLE 49.99", document.OcrText);
    }

    [Fact]
    public async Task AnEngineThatThrows_FailsTheOcr_AndTheWorkerGoesOn()
    {
        await using var host = DocumentsHost.Start();
        host.Ocr.Respond = (_, _) => throw new InvalidOperationException("Broken on purpose.");
        var first = await host.UploadOkAsync("First");

        Assert.Equal(OcrStatus.Failed, (await host.WaitForOcrAsync(first.Id)).OcrStatus);

        host.Ocr.Respond = (_, _) => OcrResult.Succeeded("Works again");
        var second = await host.UploadOkAsync("Second", files: [SampleFile.Pdf]);
        Assert.Equal(OcrStatus.Done, (await host.WaitForOcrAsync(second.Id)).OcrStatus);
    }

    [Fact]
    public async Task Retry_RunsTheOcrAgain()
    {
        await using var host = DocumentsHost.Start();
        host.Ocr.Respond = (_, _) => OcrResult.Failed("tesseract is not installed on the host.");
        var created = await host.UploadOkAsync();
        Assert.Equal(OcrStatus.Failed, (await host.WaitForOcrAsync(created.Id)).OcrStatus);

        host.Ocr.Respond = (_, _) => OcrResult.Succeeded("KETTLE 49.99");
        var response = await host.Client.PostAsync($"{Documents}/{created.Id}/ocr", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var document = await host.WaitForOcrAsync(created.Id);
        Assert.Equal((OcrStatus.Done, "KETTLE 49.99", (string?)null), (document.OcrStatus, document.OcrText, document.OcrError));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsync($"{Documents}/{Guid.NewGuid()}/ocr", null)).StatusCode);
    }

    [Fact]
    public async Task WithTheOcrOff_NewDocumentsAreNotNeeded_AndARetryIsAConflict()
    {
        await using var host = DocumentsHost.Start(new Dictionary<string, string?> { ["Documents:Ocr:Enabled"] = "false" });

        var created = await host.UploadOkAsync();
        await Task.Delay(500);

        Assert.Equal(OcrStatus.NotNeeded, (await (await host.Client.GetAsync($"{Documents}/{created.Id}")).ReadAsync<DocumentDto>()).OcrStatus);
        Assert.Empty(host.Ocr.Calls);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsync($"{Documents}/{created.Id}/ocr", null)).StatusCode);
    }

    [Fact]
    public void Clean_MakesUnixLines_WithSingleSpaces_WithoutPageBreaks_AndAtMostOneEmptyLine()
    {
        Assert.Equal("Page one\n\nPage two\nend", CommandLineOcrEngine.Clean("  \r\nPage one   \r\n\f\n\n\nPage two\rend\n\f"));
        Assert.Equal("WARRANTY CARD\nTOASTER MODEL 2031", CommandLineOcrEngine.Clean("   WARRANTY   CARD\nTOASTER \t MODEL   2031\n\f"));
    }

    // The real OCR programs (as in the host image). CI installs them; without them, these tests are skipped.

    [Fact]
    public async Task RealOcr_ReadsTheScannedPdf()
    {
        Assert.SkipUnless(Tools.Have("ocrmypdf", "pdftotext", "tesseract"), Tools.Missing);

        var result = await RealEngine().ExtractTextAsync(StoredCopy(SampleFile.Pdf), FileTypes.Pdf, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Contains("WARRANTY CARD", result.Text);
        Assert.Contains("TOASTER", result.Text);
    }

    [Theory]
    [InlineData("receipt.png", FileTypes.Png)]
    [InlineData("receipt.webp", FileTypes.Webp)]
    public async Task RealOcr_ReadsAnImage(string sample, string contentType)
    {
        Assert.SkipUnless(Tools.Have("tesseract"), Tools.Missing);

        var result = await RealEngine().ExtractTextAsync(StoredCopy(SampleFile.FromSamples(sample, contentType)), contentType,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Contains("RECEIPT", result.Text);
        Assert.Contains("KETTLE", result.Text);
    }

    [Fact]
    public async Task RealOcr_WithoutTheProgram_FailsWithAClearError()
    {
        var engine = RealEngine();

        var result = await engine.ExtractTextAsync(StoredCopy(SampleFile.Png), "image/unknown", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("no OCR", result.Error);
        var missing = await ProcessRunner.RunAsync("hestia-no-such-program", [], TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(missing.NotFound);
    }

    [Fact]
    public async Task RealOcr_ThroughTheHost_MakesTheScanSearchable()
    {
        Assert.SkipUnless(Tools.Have("ocrmypdf", "pdftotext", "tesseract"), Tools.Missing);
        await using var host = DocumentsHost.Start(engine: RealEngine());

        var created = await host.UploadOkAsync("Scan", "Warranty", files: [SampleFile.Pdf, SampleFile.Png]);
        var document = await host.WaitForOcrAsync(created.Id, TimeSpan.FromMinutes(2));

        Assert.Equal(OcrStatus.Done, document.OcrStatus);
        Assert.Contains("TOASTER", document.OcrText);
        Assert.Contains("KETTLE", document.OcrText);
        var found = await (await host.Client.GetAsync($"{Documents}?q=toaster")).ReadAsync<PaginatedList<DocumentSummaryDto>>();
        Assert.Equal(created.Id, Assert.Single(found.Items).Id);
    }

    private static CommandLineOcrEngine RealEngine() =>
        new(Options.Create(new DocumentsOptions { StoragePath = Path.GetTempPath() }), NullLogger<CommandLineOcrEngine>.Instance);

    /// <summary>A copy of the sample without its extension, as in the storage (the programs must not need it).</summary>
    private static string StoredCopy(SampleFile file)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("hestia-ocr-test-").FullName, file.Sha256);
        File.WriteAllBytes(path, file.Bytes);
        return path;
    }
}

/// <summary>Finds programs on the PATH.</summary>
public static class Tools
{
    public const string Missing = "Needs ocrmypdf, pdftotext, and tesseract on the PATH (CI installs them).";

    public static bool Have(params string[] programs) => programs.All(program =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(folder => File.Exists(Path.Combine(folder, program))));
}
