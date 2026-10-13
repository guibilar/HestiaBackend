using System.Net;
using System.Text;
using Hestia.Modules.Documents.Application;
using Hestia.Modules.Documents.Contracts;
using Hestia.Platform.Modules;
using Hestia.Testing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Documents.Tests;

/// <summary>
/// T59: the same 400 answers in Development as in Production, the readiness check of the documents folder, and the
/// command <c>documents-clean-files</c>.
/// </summary>
public class HardeningTests
{
    private const string Documents = DocumentsHost.Documents;

    // --- Step 1: a body that cannot be read is a 400 with problem details (the test host runs in Development). ---

    [Theory]
    [InlineData("""{"title":"Kettle","kind":"Spaceship"}""")]  // not an enum value
    [InlineData("""{"title":"Kettle","kind":3}""")]            // enums by name only
    [InlineData("""{"title":"Kettle","issuedOn":"tomorrow"}""")]
    [InlineData("""{"title":"Kettle",""")]                      // not JSON
    public async Task Update_WithABodyThatCannotBeRead_Is400_WithProblemDetails(string json)
    {
        await using var host = DocumentsHost.Start();
        var created = await host.UploadOkAsync();

        var response = await host.Client.PutAsync($"{Documents}/{created.Id}",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.ReadAsync<ProblemDetails>();
        Assert.Equal(400, problem.Status);
        Assert.Equal("Bad Request", problem.Title);
        // The document did not change.
        Assert.Equal("Kettle receipt", (await (await host.Client.GetAsync($"{Documents}/{created.Id}")).ReadAsync<DocumentDto>()).Title);
    }

    [Fact]
    public async Task AddLink_WithABodyThatIsNotJson_Is415_WithProblemDetails()
    {
        await using var host = DocumentsHost.Start();
        var created = await host.UploadOkAsync();

        var response = await host.Client.PostAsync($"{Documents}/{created.Id}/links", new StringContent("kind=x", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AnUploadOverTheUploadLimit_IsABadRequest_AndNothingIsStored()
    {
        await using var host = DocumentsHost.Start(new Dictionary<string, string?>
        {
            ["Documents:MaxFileSizeMb"] = "1",
            ["Documents:MaxUploadSizeMb"] = "1"
        });
        var large = new SampleFile("large.pdf", [.. "%PDF-1.7\n"u8, .. new byte[1200 * 1024]], FileTypes.Pdf);

        var response = await host.UploadAsync(DocumentsHost.Form("Too large", files: [large]));

        var problem = await UploadTests.AssertBadRequestAsync(response, "files");
        Assert.Contains("1 MB", problem.Errors["files"][0]);
        Assert.Empty(host.StoredFiles());
        Assert.Equal(0, await host.CountAsync());
    }

    // --- Step 2: the readiness check of the documents folder. ---

    [Fact]
    public async Task Ready_Is200_WhenTheDocumentsFolderIsWritable_AndLeavesNoFile()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = DocumentsHost.Start(finance.HostSettings());

        var response = await host.Client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Directory.EnumerateFiles(host.StoragePath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Ready_Is503_WhenTheDocumentsFolderIsMissing_AndLiveIs200()
    {
        await using var finance = await FakeFinance.StartAsync();
        var missing = Path.Combine(Path.GetTempPath(), $"hestia-missing-{Guid.NewGuid():N}");
        await using var host = DocumentsHost.Start(finance.HostSettings(new Dictionary<string, string?> { ["Documents:StoragePath"] = missing }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.Client.GetAsync("/health/ready")).StatusCode);
        // Liveness has no dependencies: a missing volume must not restart the host.
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/health/live")).StatusCode);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task TheCheck_IsUnhealthy_WhenTheHostCannotWriteInTmp()
    {
        var root = Directory.CreateTempSubdirectory("hestia-documents-check-").FullName;
        try
        {
            var check = new DocumentStorageHealthCheck(Storage(root));
            // "tmp" is a file, so the host cannot make the folder or write in it (also as root, unlike a file mode).
            var temp = Path.Combine(root, DocumentStorage.TempFolder);
            await File.WriteAllTextAsync(temp, "not a folder");

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Contains("cannot write", result.Description);

            // After a fix, the check is healthy again.
            File.Delete(temp);
            Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TheCheck_IsPartOfReady_AndNotOfLive()
    {
        await using var host = DocumentsHost.Start();

        var registrations = host.Factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        var registration = Assert.Single(registrations, r => r.Name == DocumentStorageHealthCheck.Name);
        Assert.Contains(Hestia.Platform.Health.HealthTags.Ready, registration.Tags);
    }

    // --- Step 3: documents-clean-files. ---

    [Fact]
    public async Task CleanFiles_ListsTheOldUnusedFiles_AndDeletesNothing_WithoutDelete()
    {
        await using var host = DocumentsHost.Start();
        var files = await Arrange(host);

        var (exitCode, output) = await RunCleanAsync(host);

        Assert.Equal(0, exitCode);
        Assert.Contains($"unused    {files.OldUnused}", output);
        Assert.Contains($"unused    {files.OldTemp}", output);
        Assert.DoesNotContain(files.Used, output);
        Assert.DoesNotContain(files.NewUnused, output);
        Assert.DoesNotContain(files.NewTemp, output);
        Assert.Contains($"skipped   {files.Other}", output);
        Assert.Contains("2 unused file(s)", output);
        Assert.All(files.All, key => Assert.True(File.Exists(Path.Combine(host.StoragePath, key)), key));
    }

    [Fact]
    public async Task CleanFiles_WithDelete_RemovesOnlyTheOldUnusedFiles_AndTheOldTempFiles()
    {
        await using var host = DocumentsHost.Start();
        var files = await Arrange(host);

        var (exitCode, output) = await RunCleanAsync(host, CleanFilesCommand.DeleteArgument);

        Assert.Equal(0, exitCode);
        Assert.Contains("Deleted 2 of 2", output);
        Assert.False(File.Exists(Path.Combine(host.StoragePath, files.OldUnused)));
        Assert.False(File.Exists(Path.Combine(host.StoragePath, files.OldTemp)));
        Assert.All(new[] { files.Used, files.NewUnused, files.NewTemp, files.Other },
            key => Assert.True(File.Exists(Path.Combine(host.StoragePath, key)), key));
        // The document still downloads its file.
        var document = await (await host.Client.GetAsync($"{Documents}/{files.DocumentId}")).ReadAsync<DocumentDto>();
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync($"{Documents}/{document.Id}/files/{document.Files[0].Id}")).StatusCode);

        // A second run finds nothing.
        var (_, again) = await RunCleanAsync(host, CleanFilesCommand.DeleteArgument);
        Assert.Contains("No unused files.", again);
    }

    [Fact]
    public async Task CleanFiles_FindsTheFileOfADocumentRowThatWasDeleted_WithoutItsFile()
    {
        // The OCR is off, so no background work changes the document at the same time.
        await using var host = DocumentsHost.Start(new Dictionary<string, string?> { ["Documents:Ocr:Enabled"] = "false" });
        var document = await host.UploadOkAsync();
        var key = document.Files[0].Sha256[..2] + "/" + document.Files[0].Sha256;
        // A stop between the row delete and the file delete: the row is gone, the file stays.
        await host.WithDbAsync(async db =>
        {
            db.Documents.Remove(await db.Documents.Include(d => d.Files).SingleAsync(d => d.Id == document.Id));
            return await db.SaveChangesAsync();
        });
        MakeOld(Path.Combine(host.StoragePath, key));

        var (exitCode, output) = await RunCleanAsync(host, CleanFilesCommand.DeleteArgument);

        Assert.Equal(0, exitCode);
        Assert.Contains($"deleted   {key}", output);
        Assert.Empty(host.StoredFiles());
    }

    [Fact]
    public async Task CleanFiles_WithAnUnknownArgument_Fails_AndDeletesNothing()
    {
        await using var host = DocumentsHost.Start();
        var files = await Arrange(host);

        var (exitCode, output) = await RunCleanAsync(host, "--force");

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown argument", output);
        Assert.All(files.All, key => Assert.True(File.Exists(Path.Combine(host.StoragePath, key)), key));
    }

    [Fact]
    public async Task StoringContentThatIsStoredAlready_GivesTheFileANewTime()
    {
        var root = Directory.CreateTempSubdirectory("hestia-documents-touch-").FullName;
        try
        {
            var storage = Storage(root);
            var first = await storage.StoreAsync(new MemoryStream(SampleFile.Png.Bytes), long.MaxValue, CancellationToken.None);
            var path = storage.PathOf(first.Key);
            MakeOld(path);

            var second = await storage.StoreAsync(new MemoryStream(SampleFile.Png.Bytes), long.MaxValue, CancellationToken.None);

            Assert.True(first.Created);
            Assert.False(second.Created);
            // So documents-clean-files does not remove it before the rows of the new upload are saved.
            Assert.True(File.GetLastWriteTimeUtc(path) > DateTime.UtcNow - CleanFilesCommand.MinimumAge);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record ArrangedFiles(Guid DocumentId, string Used, string OldUnused, string NewUnused, string OldTemp,
        string NewTemp, string Other)
    {
        public IReadOnlyList<string> All => [Used, OldUnused, NewUnused, OldTemp, NewTemp, Other];
    }

    /// <summary>
    /// A document with its file (old, but used), and on the volume: an old and a new unused file, an old and a new file
    /// in tmp/, and a file that is not a document file.
    /// </summary>
    private static async Task<ArrangedFiles> Arrange(DocumentsHost host)
    {
        var document = await host.UploadOkAsync();
        var used = Assert.Single(host.StoredFiles());
        MakeOld(Path.Combine(host.StoragePath, used));

        var oldUnused = Write(host, Key('a'), old: true);
        var newUnused = Write(host, Key('b'), old: false);
        var oldTemp = Write(host, $"{DocumentStorage.TempFolder}/{Guid.NewGuid():N}.partial", old: true);
        var newTemp = Write(host, $"{DocumentStorage.TempFolder}/{Guid.NewGuid():N}.partial", old: false);
        var other = Write(host, "notes/readme.txt", old: true);
        return new ArrangedFiles(document.Id, used, oldUnused, newUnused, oldTemp, newTemp, other);

        static string Key(char c) => $"{c}{c}/{new string(c, 64)}";
    }

    private static string Write(DocumentsHost host, string key, bool old)
    {
        var path = Path.Combine(host.StoragePath, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"content of {key}");
        if (old)
        {
            MakeOld(path);
        }
        return key;
    }

    private static void MakeOld(string path) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2));

    private static DocumentStorage Storage(string root) =>
        new(Options.Create(new DocumentsOptions { StoragePath = root }));

    private static async Task<(int ExitCode, string Output)> RunCleanAsync(DocumentsHost host, params string[] arguments)
    {
        _ = host.Client;
        var command = host.Factory.Services.GetServices<IModuleCommand>().Single(c => c.Name == "documents-clean-files");
        var output = new StringWriter();
        var exitCode = await command.RunAsync(host.Factory.Services, arguments, TextReader.Null, output, CancellationToken.None);
        return (exitCode, output.ToString());
    }
}
