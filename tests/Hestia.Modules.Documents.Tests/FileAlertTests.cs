using System.Net;
using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Documents.Application;
using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Documents.Tests;

/// <summary>
/// T80: the alert <c>documents.files-missing</c>, from the result of the scheduled file check (<see cref="FileCheckWorker"/>).
/// </summary>
public class FileAlertTests
{
    private const string Key = DocumentFileAlerts.Key;

    /// <summary>The OCR is off, so no background work reads the files at the same time.</summary>
    private static DocumentsHost Start() => DocumentsHost.Start(new Dictionary<string, string?>
    {
        ["Documents:Ocr:Enabled"] = "false",
        ["Alerts:TimeZone"] = "America/Edmonton"
    });

    [Fact]
    public async Task AMissingFile_RaisesABadAlert_AfterTheFileCheck_WithoutARefresh()
    {
        await using var host = Start();
        var lost = await host.UploadOkAsync(title: "Lost", files: [SampleFile.Png]);
        await host.UploadOkAsync(title: "Kept", files: [SampleFile.Pdf]);
        File.Delete(Path.Combine(host.StoragePath, StorageKey(lost)));
        // The first request after the start checks all rules: no alert yet, because no file check ran.
        Assert.DoesNotContain((await AlertsAsync(host)).Alerts, a => a.Id == Key);

        await Worker(host).RunOnceAsync(CancellationToken.None);

        // The worker asks for a check of the source; no refresh is needed.
        var list = await Wait.UntilAsync(() => AlertsAsync(host), l => l.Alerts.Any(a => a.Id == Key));
        var alert = Assert.Single(list.Alerts, a => a.Id == Key);
        Assert.Equal(("Documents", "files-missing", AlertSeverity.Bad, (string?)null),
            (alert.Module, alert.Kind, alert.Severity, alert.Link));
        Assert.Equal(AlertValue.Count(1), alert.Values["count"]);
        Assert.Equal(AlertValue.Day(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("America/Edmonton")).DateTime)), alert.Values["date"]);
        Assert.Contains("documents-clean-files --check-missing", alert.Text);
    }

    [Fact]
    public async Task TheAlert_Resolves_WhenTheFileIsBack_WithoutANewFileCheck()
    {
        await using var host = Start();
        var lost = await host.UploadOkAsync(title: "Lost", files: [SampleFile.Png]);
        var path = Path.Combine(host.StoragePath, StorageKey(lost));
        File.Delete(path);
        await Worker(host).RunOnceAsync(CancellationToken.None);
        await CheckAsync(host);
        Assert.Contains((await AlertsAsync(host)).Alerts, a => a.Id == Key);

        // The user restores the file from a backup.
        await File.WriteAllBytesAsync(path, SampleFile.Png.Bytes);
        await CheckAsync(host);

        Assert.DoesNotContain((await AlertsAsync(host)).Alerts, a => a.Id == Key);
    }

    [Fact]
    public async Task TheAlert_Resolves_WhenTheDocumentIsDeleted_AndCountsOnlyTheRowsThatAreStillMissing()
    {
        await using var host = Start();
        var first = await host.UploadOkAsync(title: "First", files: [SampleFile.Png]);
        var second = await host.UploadOkAsync(title: "Second", files: [SampleFile.Pdf]);
        File.Delete(Path.Combine(host.StoragePath, StorageKey(first)));
        File.Delete(Path.Combine(host.StoragePath, StorageKey(second)));
        await Worker(host).RunOnceAsync(CancellationToken.None);
        await CheckAsync(host);
        Assert.Equal(AlertValue.Count(2), Assert.Single((await AlertsAsync(host)).Alerts, a => a.Id == Key).Values["count"]);

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{DocumentsHost.Documents}/{first.Id}")).StatusCode);
        await CheckAsync(host);
        Assert.Equal(AlertValue.Count(1), Assert.Single((await AlertsAsync(host)).Alerts, a => a.Id == Key).Values["count"]);

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{DocumentsHost.Documents}/{second.Id}")).StatusCode);
        await CheckAsync(host);
        Assert.DoesNotContain((await AlertsAsync(host)).Alerts, a => a.Id == Key);
    }

    [Fact]
    public async Task ACleanVolume_GivesNoAlert_AndTheModuleIsComplete()
    {
        await using var host = Start();
        await host.UploadOkAsync();

        // Before the first file check, and after it.
        await CheckAsync(host);
        Assert.Equal(AlertModuleStatus.Complete, Assert.Single((await AlertsAsync(host)).Modules, m => m.Module == "Documents").Status);
        await Worker(host).RunOnceAsync(CancellationToken.None);
        await CheckAsync(host);

        var list = await AlertsAsync(host);
        Assert.DoesNotContain(list.Alerts, a => a.Id == Key);
        Assert.Equal(AlertModuleStatus.Complete, Assert.Single(list.Modules, m => m.Module == "Documents").Status);
        Assert.Empty(Assert.IsType<FileCheckResult>(host.Factory.Services.GetRequiredService<FileCheckState>().Last).Missing);
    }

    private static FileCheckWorker Worker(DocumentsHost host)
    {
        _ = host.Client;
        return host.Factory.Services.GetRequiredService<FileCheckWorker>();
    }

    private static Task CheckAsync(DocumentsHost host) =>
        host.Factory.Services.GetRequiredService<IAlertChecks>().CheckAsync(DocumentFileAlerts.SourceName);

    private static async Task<AlertList> AlertsAsync(DocumentsHost host) =>
        await (await host.Client.GetAsync("/api/alerts")).ReadAsync<AlertList>();

    private static string StorageKey(Contracts.DocumentDto document) =>
        document.Files[0].Sha256[..2] + "/" + document.Files[0].Sha256;
}
