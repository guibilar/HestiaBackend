using System.Globalization;
using Hestia.Modules.Today.Contracts;
using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hestia.Modules.Documents.Tests;

/// <summary>The Documents items of "Needs attention" on Today: documents that expire soon, or expired a short time ago.</summary>
public class TodayCardTests
{
    /// <summary>A clock that stops at one moment.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // Noon in Toronto on October 13th, 2026.
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 13);

    [Fact]
    public async Task ExpiringAndRecentlyExpiredDocuments_AreAttentionItems_SoonestFirst()
    {
        await using var host = DocumentsHost.Start(new Dictionary<string, string?> { ["Today:TimeZone"] = "America/Toronto" },
            services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTime(Now));
            });
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
        await using var host = DocumentsHost.Start(new Dictionary<string, string?>
            {
                ["Today:TimeZone"] = "America/Toronto",
                ["Documents:Expiry:SoonDays"] = "60",
                ["Documents:Expiry:ExpiredDays"] = "0"
            },
            services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTime(Now));
            });
        var later = await Upload(host, "Later", Today.AddDays(45));
        await Upload(host, "Yesterday", Today.AddDays(-1));

        var today = await (await host.Client.GetAsync("/api/today?refresh=true")).ReadAsync<TodayDto>();

        Assert.Equal(new[] { $"documents.expiring.{later}" }, today.Cards.Where(card => card.Module == "Documents").Select(card => card.Id));
    }

    private static async Task<Guid> Upload(DocumentsHost host, string title, DateOnly? expiresOn) =>
        (await host.UploadOkAsync(title, "Other", expiresOn: expiresOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).Id;
}
