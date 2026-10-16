using System.Net.Http.Json;
using Hestia.Modules.Calendar.Contracts;
using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hestia.Modules.Calendar.Tests;

/// <summary>A clock that a test sets.</summary>
public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// The host with all modules, with a clock at noon in Toronto on Friday, October 16th, 2026 (<see cref="Today"/>), and the
/// time zone of all modules set to Toronto. Summer time ends there on Sunday, November 1st, 2026.
/// </summary>
public sealed class CalendarHost : IAsyncDisposable
{
    public const string Calendar = "/api/calendar";
    public const string TimeZone = "America/Toronto";

    public static readonly DateTimeOffset Now = new(2026, 10, 16, 16, 0, 0, TimeSpan.Zero);
    public static readonly DateOnly Today = new(2026, 10, 16);

    private HttpClient? _client;

    private CalendarHost(HestiaApiFactory factory) => Factory = factory;

    public HestiaApiFactory Factory { get; }
    public HttpClient Client => _client ??= Factory.CreateClient();

    public static CalendarHost Start(bool signedIn = true)
    {
        var clock = new TestClock(Now);
        var factory = new HestiaApiFactory(
            new Dictionary<string, string?> { ["Hestia:TimeZone"] = TimeZone, ["Documents:Ocr:Enabled"] = "false" },
            services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            }, signedIn);
        return new CalendarHost(factory);
    }

    /// <summary>A moment in Toronto.</summary>
    public static DateTimeOffset Toronto(int year, int month, int day, int hour, int minute = 0)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        var local = new DateTime(year, month, day, hour, minute, 0);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    public Task<HttpResponseMessage> CreateAsync(CalendarEventRequest request) =>
        Client.PostAsJsonAsync($"{Calendar}/events", request, HestiaApiFactory.Json);

    public async Task<CalendarEventDto> AddAsync(CalendarEventRequest request)
    {
        var response = await CreateAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<CalendarEventDto>();
    }

    public async Task<CalendarDto> ViewAsync(DateOnly from, DateOnly to)
    {
        var response = await Client.GetAsync($"{Calendar}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<CalendarDto>();
    }

    public async Task<CalendarImportResultDto> ImportAsync(string ics, bool dryRun = false)
    {
        var response = await Client.PostAsJsonAsync($"{Calendar}/import", new CalendarImportRequest(ics, dryRun), HestiaApiFactory.Json);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<CalendarImportResultDto>();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await Factory.DisposeAsync();
    }
}
