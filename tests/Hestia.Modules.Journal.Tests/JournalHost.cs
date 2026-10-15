using System.Net.Http.Json;
using Hestia.Modules.Journal.Contracts;
using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hestia.Modules.Journal.Tests;

/// <summary>A clock that a test sets and moves.</summary>
public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// The host with all modules (<see cref="HestiaApiFactory"/>), with a clock that the test controls: noon in Toronto on
/// Saturday, October 10th, 2026 (<see cref="Today"/>), and the time zone of all modules set to Toronto.
/// </summary>
public sealed class JournalHost : IAsyncDisposable
{
    public const string Journal = "/api/journal";
    public const string TimeZone = "America/Toronto";

    /// <summary>Noon in Toronto on October 10th, 2026.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 10, 16, 0, 0, TimeSpan.Zero);

    public static readonly DateOnly Today = new(2026, 10, 10);

    private HttpClient? _client;

    private JournalHost(HestiaApiFactory factory, TestClock clock)
    {
        Factory = factory;
        Clock = clock;
    }

    public HestiaApiFactory Factory { get; }
    public TestClock Clock { get; }
    public HttpClient Client => _client ??= Factory.CreateClient();

    public static JournalHost Start(IReadOnlyDictionary<string, string?>? settings = null, bool signedIn = true, DateTimeOffset? now = null)
    {
        var clock = new TestClock(now ?? Now);
        var all = new Dictionary<string, string?> { ["Hestia:TimeZone"] = TimeZone, ["Documents:Ocr:Enabled"] = "false" };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            all[key] = value;
        }
        var factory = new HestiaApiFactory(all, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }, signedIn);
        return new JournalHost(factory, clock);
    }

    public static string EntryUrl(DateOnly day) => $"{Journal}/entries/{day:yyyy-MM-dd}";

    public Task<HttpResponseMessage> PutAsync(DateOnly day, string? text) =>
        Client.PutAsJsonAsync(EntryUrl(day), new SaveJournalEntryRequest(text), HestiaApiFactory.Json);

    /// <summary>Writes the entry of a day, and fails the test when the host refuses it.</summary>
    public async Task<JournalEntryDto> WriteAsync(DateOnly day, string text)
    {
        var response = await PutAsync(day, text);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<JournalEntryDto>();
    }

    public async Task<JournalSummaryDto> SummaryAsync() =>
        await (await Client.GetAsync($"{Journal}/summary")).ReadAsync<JournalSummaryDto>();

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await Factory.DisposeAsync();
    }
}
