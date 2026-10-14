using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Alerts.Infrastructure;
using Hestia.Modules.Documents.Application;
using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Today.Contracts;
using Hestia.Platform.Events;
using Hestia.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;

namespace Hestia.Modules.Documents.Tests;

/// <summary>
/// The Documents alerts (T15): documents that expire soon, or expired a short time ago. The Alerts module stores them;
/// Today shows them as "Needs attention" items, with the same ids, texts, and links as before T15.
/// </summary>
public class ExpiryAlertTests
{
    /// <summary>A clock that stops at one moment.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // Noon in Toronto on October 13th, 2026.
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 13);

    private static DocumentsHost Start(Dictionary<string, string?>? settings = null)
    {
        var all = new Dictionary<string, string?> { ["Today:TimeZone"] = "America/Toronto", ["Alerts:TimeZone"] = "America/Toronto" };
        foreach (var (key, value) in settings ?? [])
        {
            all[key] = value;
        }
        return DocumentsHost.Start(all, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTime(Now));
        });
    }

    [Fact]
    public async Task ExpiringAndRecentlyExpiredDocuments_AreAttentionItemsOnToday_SoonestFirst()
    {
        await using var host = Start();
        var inTenDays = await Upload(host, "Lease", Today.AddDays(10));
        var inThreeDays = await Upload(host, "Car insurance", Today.AddDays(3));
        var expired = await Upload(host, "Toaster warranty", Today.AddDays(-3));
        var expiresToday = await Upload(host, "Passport copy", Today);
        await Upload(host, "Later", Today.AddDays(31));
        await Upload(host, "Long ago", Today.AddDays(-8));
        await Upload(host, "No expiry", null);

        var today = await (await host.Client.GetAsync("/api/today?refresh=true")).ReadAsync<TodayDto>();

        Assert.Equal(new TodayModuleDto("Documents", TodayModuleStatus.Complete), Assert.Single(today.Modules, m => m.Module == "Documents"));
        var cards = today.Cards.Where(card => card.Module == "Documents").ToList();
        Assert.Equal(
            new[] { $"documents.expired.{expired}", $"documents.expiring.{expiresToday}", $"documents.expiring.{inThreeDays}", $"documents.expiring.{inTenDays}" },
            cards.Select(card => card.Id));
        Assert.All(cards, card => Assert.Equal(TodayCardType.Attention, card.Type));

        var gone = cards[0];
        Assert.Equal(("expired", "{document} expired on {date}", TodaySeverity.Bad, $"/documents/{expired}"),
            (gone.Kind, gone.Text, gone.Severity, gone.Link));
        Assert.Equal(TodayValue.Plain("Toaster warranty"), gone.Values["document"]);
        Assert.Equal(TodayValue.Day(Today.AddDays(-3)), gone.Values["date"]);
        Assert.Equal(TodayValue.Count(3), gone.Values["days"]);

        Assert.Equal(("{document} expires today", TodaySeverity.Bad), (cards[1].Text, cards[1].Severity));
        Assert.Equal(("{document} expires on {date}", TodaySeverity.Bad), (cards[2].Text, cards[2].Severity));
        var lease = cards[3];
        Assert.Equal(("expiring", "{document} expires on {date}", TodaySeverity.Warning), (lease.Kind, lease.Text, lease.Severity));
        Assert.Equal(TodayValue.Count(10), lease.Values["days"]);
    }

    [Fact]
    public async Task TheWindows_AreSettings()
    {
        await using var host = Start(new()
        {
            ["Documents:Expiry:SoonDays"] = "60",
            ["Documents:Expiry:ExpiredDays"] = "0"
        });
        var later = await Upload(host, "Later", Today.AddDays(45));
        await Upload(host, "Yesterday", Today.AddDays(-1));

        var today = await (await host.Client.GetAsync("/api/today?refresh=true")).ReadAsync<TodayDto>();

        Assert.Equal(new[] { $"documents.expiring.{later}" }, today.Cards.Where(card => card.Module == "Documents").Select(card => card.Id));
    }

    [Fact]
    public async Task ADocumentEvent_ChecksThatDocument_WithoutARefresh()
    {
        await using var host = Start();
        // The first request after the start checks all rules.
        await AlertsAsync(host);

        // A new document: its DocumentAdded event raises its alert.
        var lease = await Upload(host, "Lease", Today.AddDays(10));
        var key = $"documents.expiring.{lease}";
        var raised = await Wait.UntilAsync(() => AlertsAsync(host), list => list.Alerts.Any(alert => alert.Id == key));
        var alert = Assert.Single(raised.Alerts, a => a.Id == key);
        Assert.Equal(("Documents", "expiring", "{document} expires on {date}", AlertSeverity.Warning, $"/documents/{lease}"),
            (alert.Module, alert.Kind, alert.Text, alert.Severity, alert.Link));
        Assert.Equal(AlertValue.Plain("Lease"), alert.Values["document"]);

        // A later expiry date (DocumentChanged): no alert now.
        var update = await host.Client.PutAsJsonAsync($"{DocumentsHost.Documents}/{lease}", new UpdateDocumentRequest(
            "Lease", DocumentKind.Lease, [], null, Today.AddDays(400), null), HestiaApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var resolved = await Wait.UntilAsync(() => AlertsAsync(host), list => list.Alerts.All(a => a.Id != key));
        Assert.DoesNotContain(resolved.Alerts, a => a.Id == key);

        // A delete (DocumentDeleted) resolves the alert of the document.
        var warranty = await Upload(host, "Toaster warranty", Today.AddDays(-2));
        var expiredKey = $"documents.expired.{warranty}";
        await Wait.UntilAsync(() => AlertsAsync(host), list => list.Alerts.Any(a => a.Id == expiredKey));
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{DocumentsHost.Documents}/{warranty}")).StatusCode);
        var gone = await Wait.UntilAsync(() => AlertsAsync(host), list => list.Alerts.All(a => a.Id != expiredKey));
        Assert.DoesNotContain(gone.Alerts, a => a.Id == expiredKey);
        Assert.Equal(AlertModuleStatus.Complete, Assert.Single(gone.Modules, m => m.Module == "Documents").Status);
    }

    [Fact]
    public async Task TheSameDocumentEvent_Twice_GivesOneAlert()
    {
        await using var host = Start();
        var id = await Upload(host, "Car insurance", Today.AddDays(3));
        var key = $"documents.expiring.{id}";
        var first = Assert.Single((await Wait.UntilAsync(() => AlertsAsync(host), list => list.Alerts.Any(a => a.Id == key))).Alerts,
            a => a.Id == key);
        var again = new DocumentAdded(id, "Car insurance", DocumentKind.Insurance, [], null, Today.AddDays(3), 1, Now);

        await DeliverAsync(host, again);
        await DeliverAsync(host, again);

        var list = await AlertsAsync(host);
        Assert.Equal(first.AlertId, Assert.Single(list.Alerts, a => a.Id == key).AlertId);
        Assert.Equal(1, await host.Factory.WithScopeAsync(services => services.GetRequiredService<AlertsDbContext>().Alerts
            .CountAsync(alert => alert.Key == key)));
        Assert.Equal(1, await host.Factory.WithScopeAsync(services => services.GetRequiredService<AlertsDbContext>().Set<InboxEntry>()
            .CountAsync(entry => entry.EventId == again.EventId && entry.Consumer == DocumentAlertsConsumer.InboxName)));
    }

    [Fact]
    public void TheRules_GiveTheKey_TheText_AndTheSeverity_OfEachCase()
    {
        Assert.Equal(
            ("documents.expiring.00000000-0000-0000-0000-000000000001", "{document} expires today", AlertSeverity.Bad),
            Fields(DocumentExpiryAlerts.Alert(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Passport copy", Today, Today)));
        Assert.Equal(
            ("documents.expiring.00000000-0000-0000-0000-000000000002", "{document} expires on {date}", AlertSeverity.Bad),
            Fields(DocumentExpiryAlerts.Alert(Guid.Parse("00000000-0000-0000-0000-000000000002"), "Car insurance", Today.AddDays(7), Today)));
        Assert.Equal(
            ("documents.expiring.00000000-0000-0000-0000-000000000003", "{document} expires on {date}", AlertSeverity.Warning),
            Fields(DocumentExpiryAlerts.Alert(Guid.Parse("00000000-0000-0000-0000-000000000003"), "Lease", Today.AddDays(8), Today)));
        Assert.Equal(
            ("documents.expired.00000000-0000-0000-0000-000000000004", "{document} expired on {date}", AlertSeverity.Bad),
            Fields(DocumentExpiryAlerts.Alert(Guid.Parse("00000000-0000-0000-0000-000000000004"), "Warranty", Today.AddDays(-1), Today)));

        static (string, string, AlertSeverity) Fields(AlertCandidate alert) => (alert.Key, alert.Text, alert.Severity);
    }

    private static async Task<AlertList> AlertsAsync(DocumentsHost host) =>
        await (await host.Client.GetAsync("/api/alerts")).ReadAsync<AlertList>();

    private static async Task DeliverAsync(DocumentsHost host, object message)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(message);
    }

    private static async Task<Guid> Upload(DocumentsHost host, string title, DateOnly? expiresOn) =>
        (await host.UploadOkAsync(title, "Other", expiresOn: expiresOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).Id;
}
