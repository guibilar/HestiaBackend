using Hestia.Modules.Documents.Application;
using Hestia.Platform.Modules;
using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hestia.Modules.Documents.Tests;

/// <summary>
/// T64: the scheduled check of the documents volume (<see cref="FileCheckWorker"/>) and
/// <c>documents-clean-files --check-missing</c>.
/// </summary>
public class FileCheckTests
{
    /// <summary>The OCR is off, so no background work reads the files at the same time.</summary>
    private static readonly Dictionary<string, string?> NoOcr = new() { ["Documents:Ocr:Enabled"] = "false" };

    // --- Step 2: unused files. ---

    [Fact]
    public async Task TheCheck_LogsAWarning_WithTheCountAndTheBytes_OfTheUnusedFiles_AndDeletesNothing()
    {
        var logs = new LogCapture();
        await using var host = Start(logs);
        await host.UploadOkAsync();
        var oldUnused = Write(host, Key('a'), "12345", old: true);
        var oldTemp = Write(host, $"{DocumentStorage.TempFolder}/{Guid.NewGuid():N}.partial", "123", old: true);
        var newUnused = Write(host, Key('b'), "new", old: false);

        var (unused, missing) = await Worker(host).RunOnceAsync(CancellationToken.None);

        Assert.Equal([oldUnused, oldTemp], unused.Unused.Select(file => file.Key).ToList());
        Assert.Equal(8, unused.Bytes);
        Assert.Empty(missing);
        var warning = Assert.Single(logs.Find("unused file(s)", LogLevel.Warning));
        Assert.Contains("2 unused file(s)", warning.Message);
        Assert.Contains("8 bytes", warning.Message);
        Assert.All(new[] { oldUnused, oldTemp, newUnused }, key => Assert.True(File.Exists(Path.Combine(host.StoragePath, key)), key));
    }

    [Fact]
    public async Task TheCheck_LogsNoWarning_WhenTheVolumeIsClean()
    {
        var logs = new LogCapture();
        await using var host = Start(logs);
        await host.UploadOkAsync();

        var (unused, missing) = await Worker(host).RunOnceAsync(CancellationToken.None);

        Assert.Empty(unused.Unused);
        Assert.Empty(missing);
        Assert.Empty(logs.Find("Documents:", LogLevel.Warning));
        Assert.Single(logs.Find("No unused files and no missing files", LogLevel.Information));
    }

    // --- Step 3: rows whose file is missing. ---

    [Fact]
    public async Task TheCheck_LogsAWarning_ForEachRowWhoseFileIsMissing()
    {
        var logs = new LogCapture();
        await using var host = Start(logs);
        var lost = await host.UploadOkAsync(title: "Lost", files: [SampleFile.Png]);
        var kept = await host.UploadOkAsync(title: "Kept", files: [SampleFile.Pdf]);
        File.Delete(Path.Combine(host.StoragePath, StorageKey(lost)));

        var (_, missing) = await Worker(host).RunOnceAsync(CancellationToken.None);

        var row = Assert.Single(missing);
        Assert.Equal(lost.Id, row.DocumentId);
        Assert.Equal(lost.Files[0].Id, row.FileId);
        var warning = Assert.Single(logs.Find("is not on the documents volume", LogLevel.Warning));
        Assert.Contains(lost.Id.ToString(), warning.Message);
        Assert.Contains(lost.Files[0].Id.ToString(), warning.Message);
        Assert.DoesNotContain(kept.Id.ToString(), warning.Message);
    }

    [Fact]
    public async Task CheckMissing_PrintsTheRowsWhoseFileIsMissing_WithExitCode2_AndDeletesNothing()
    {
        await using var host = Start();
        var lost = await host.UploadOkAsync(title: "Lost", files: [SampleFile.Png]);
        await host.UploadOkAsync(title: "Kept", files: [SampleFile.Pdf]);
        var unused = Write(host, Key('c'), "unused", old: true);
        File.Delete(Path.Combine(host.StoragePath, StorageKey(lost)));

        var (exitCode, output) = await RunCleanAsync(host, CleanFilesCommand.CheckMissingArgument);

        Assert.Equal(2, exitCode);
        Assert.Contains($"missing   {StorageKey(lost)} (document {lost.Id}, file {lost.Files[0].Id})", output);
        Assert.Contains("1 file(s) of 1 document(s)", output);
        // The other check does not run: the unused file is not listed, and it stays.
        Assert.DoesNotContain(unused, output);
        Assert.True(File.Exists(Path.Combine(host.StoragePath, unused)));
        // The row stays: only the user decides to restore the file or to delete the document.
        Assert.Equal(2, await host.CountAsync());
    }

    [Fact]
    public async Task CheckMissing_SaysSo_WhenNoFileIsMissing()
    {
        await using var host = Start();
        await host.UploadOkAsync();

        var (exitCode, output) = await RunCleanAsync(host, CleanFilesCommand.CheckMissingArgument);

        Assert.Equal(0, exitCode);
        Assert.Contains("No missing files.", output);
    }

    [Fact]
    public async Task CheckMissing_WithDelete_Fails_AndDeletesNothing()
    {
        await using var host = Start();
        await host.UploadOkAsync();
        var unused = Write(host, Key('d'), "unused", old: true);

        var (exitCode, output) = await RunCleanAsync(host, CleanFilesCommand.CheckMissingArgument, CleanFilesCommand.DeleteArgument);

        Assert.Equal(1, exitCode);
        Assert.Contains("not both", output);
        Assert.True(File.Exists(Path.Combine(host.StoragePath, unused)));
    }

    // --- The schedule and the settings. ---

    [Fact]
    public async Task TheHost_RunsTheCheck_AfterTheFirstDelay()
    {
        var logs = new LogCapture();
        await using var host = Start(logs, new Dictionary<string, string?> { ["Documents:FileCheck:FirstDelay"] = "00:00:00.200" });
        Write(host, Key('e'), "unused", old: true);

        _ = host.Client; // starts the host and its background services
        var warning = await Eventually(() => logs.Find("unused file(s)", LogLevel.Warning));

        Assert.Contains("1 unused file(s)", Assert.Single(warning).Message);
    }

    [Fact]
    public async Task TheHost_DoesNotRunTheCheck_WhenItIsOff()
    {
        var logs = new LogCapture();
        await using var host = Start(logs, new Dictionary<string, string?>
        {
            ["Documents:FileCheck:Enabled"] = "false",
            ["Documents:FileCheck:FirstDelay"] = "00:00:00"
        });
        Write(host, Key('f'), "unused", old: true);

        _ = host.Client;
        await Eventually(() => logs.Find("file check is off"));
        await Task.Delay(500);

        Assert.Empty(logs.Find("unused file(s)"));
    }

    [Theory]
    [InlineData("Documents:FileCheck:Interval", "00:00:00")]
    [InlineData("Documents:FileCheck:Interval", "60.00:00:00")]
    [InlineData("Documents:FileCheck:FirstDelay", "-00:01:00")]
    public async Task TheHost_DoesNotStart_WithInvalidFileCheckSettings(string key, string value)
    {
        await using var host = Start(more: new Dictionary<string, string?> { [key] = value });

        var error = Assert.ThrowsAny<Exception>(() => host.Client);

        Assert.Contains("FileCheck", error.ToString());
    }

    private static DocumentsHost Start(LogCapture? logs = null, IReadOnlyDictionary<string, string?>? more = null)
    {
        var settings = new Dictionary<string, string?>(NoOcr);
        foreach (var (key, value) in more ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }
        return DocumentsHost.Start(settings, services =>
        {
            if (logs is not null)
            {
                services.AddSingleton<ILoggerProvider>(logs);
            }
        });
    }

    private static FileCheckWorker Worker(DocumentsHost host)
    {
        _ = host.Client;
        return host.Factory.Services.GetRequiredService<FileCheckWorker>();
    }

    private static string StorageKey(Contracts.DocumentDto document) =>
        document.Files[0].Sha256[..2] + "/" + document.Files[0].Sha256;

    private static string Key(char c) => $"{c}{c}/{new string(c, 64)}";

    private static string Write(DocumentsHost host, string key, string content, bool old)
    {
        var path = Path.Combine(host.StoragePath, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (old)
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2));
        }
        return key;
    }

    private static async Task<IReadOnlyList<CapturedLog>> Eventually(Func<IReadOnlyList<CapturedLog>> read)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var lines = read();
            if (lines.Count > 0 || DateTimeOffset.UtcNow > deadline)
            {
                return lines;
            }
            await Task.Delay(50);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunCleanAsync(DocumentsHost host, params string[] arguments)
    {
        _ = host.Client;
        var command = host.Factory.Services.GetServices<IModuleCommand>().Single(c => c.Name == "documents-clean-files");
        var output = new StringWriter();
        var exitCode = await command.RunAsync(host.Factory.Services, arguments, TextReader.Null, output, CancellationToken.None);
        return (exitCode, output.ToString());
    }
}
