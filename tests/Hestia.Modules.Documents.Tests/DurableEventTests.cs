using System.Text.Json;
using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Documents.Infrastructure;
using Hestia.Platform.Events;
using Hestia.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Documents.Tests;

/// <summary>
/// The Documents events are durable from the first migration (T38): each event is a row of <c>documents.EventOutbox</c>,
/// stored with the change, and the host's sender delivers it, also after a restart.
/// </summary>
public class DurableEventTests
{
    [Fact]
    public async Task DocumentAdded_ReachesItsConsumers_AndLeavesTheOutbox()
    {
        await using var host = DocumentsHost.Start();

        var document = await host.UploadOkAsync("Kettle receipt", "Receipt", ["kitchen"], expiresOn: "2027-01-31");

        var added = await host.Factory.Events.WaitForAsync<DocumentAdded>();
        Assert.Equal((document.Id, "Kettle receipt", DocumentKind.Receipt, (DateOnly?)new DateOnly(2027, 1, 31), 1, 1),
            (added.DocumentId, added.Title, added.Kind, added.ExpiresOn, added.FileCount, added.Version));
        Assert.Equal(new[] { "kitchen" }, added.Tags);
        await host.Factory.Events.WaitForAsync<DocumentChanged>(e => e.Change == DocumentChange.OcrText);
        await Wait.UntilAsync(() => OutboxAsync(host), rows => rows.Count == 0);
        Assert.Empty(await OutboxAsync(host));
    }

    [Fact]
    public async Task DocumentAdded_IsStoredInTheOutbox_WithTheDocument_AndSurvivesARestart()
    {
        var storage = Directory.CreateTempSubdirectory("hestia-documents-test-").FullName;
        var ocr = new FakeOcrEngine();
        // The first host commits the document and its event, and stops before it sends the event. Its OCR is off, so
        // the outbox has only the DocumentAdded row.
        var withoutSender = DocumentsHost.Settings(storage, new Dictionary<string, string?>
        {
            ["Events:Outbox:Enabled"] = "false",
            ["Documents:Ocr:Enabled"] = "false"
        });
        var first = new HestiaApiFactory(withoutSender, DocumentsHost.Services(ocr));
        var form = DocumentsHost.Form("Kettle receipt", "Receipt", files: [SampleFile.Png]);
        var document = await (await first.CreateClient().PostAsync(DocumentsHost.Documents, form)).ReadAsync<DocumentDto>();

        var row = Assert.Single(await first.WithScopeAsync(ReadOutboxAsync));
        Assert.Equal(typeof(DocumentAdded).FullName, row.EventType);
        var stored = JsonSerializer.Deserialize<DocumentAdded>(row.Payload)!;
        Assert.Equal(document.Id, stored.DocumentId);
        Assert.Empty(await first.Events.AfterQuietPeriodAsync<DocumentAdded>());

        // The next host, on the same database and storage, sends it with the same event id.
        await using var second = first.Restarted(DocumentsHost.Settings(storage), DocumentsHost.Services(ocr));
        await first.DisposeAsync();
        second.CreateClient();

        var received = await second.Events.WaitForAsync<DocumentAdded>(e => e.DocumentId == document.Id);
        Assert.Equal(stored.EventId, received.EventId);
        await Wait.UntilAsync(() => second.WithScopeAsync(ReadOutboxAsync), rows => rows.Count == 0);
        Directory.Delete(storage, recursive: true);
    }

    private static Task<List<OutboxMessage>> OutboxAsync(DocumentsHost host) =>
        host.Factory.WithScopeAsync(ReadOutboxAsync);

    private static Task<List<OutboxMessage>> ReadOutboxAsync(IServiceProvider services) =>
        services.GetRequiredService<DocumentsDbContext>().Set<OutboxMessage>().AsNoTracking().OrderBy(row => row.Id).ToListAsync();
}
