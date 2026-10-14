using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.Home.Infrastructure;
using Hestia.Platform.Paging;
using Hestia.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;

namespace Hestia.Modules.Home.Tests;

/// <summary>A clock that a test sets and moves.</summary>
public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// The host with all modules (<see cref="HestiaApiFactory"/>), with a clock that the test controls: noon in Toronto on
/// October 14th, 2026 (<see cref="Today"/>), and the time zone of the Home module set to Toronto.
/// </summary>
public sealed class HomeHost : IAsyncDisposable
{
    public const string Home = "/api/home";
    public const string Items = "/api/home/items";
    public const string Rooms = "/api/home/rooms";
    public const string TimeZone = "America/Toronto";

    /// <summary>Noon in Toronto on October 14th, 2026.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 14, 16, 0, 0, TimeSpan.Zero);

    public static readonly DateOnly Today = new(2026, 10, 14);

    private HttpClient? _client;

    private HomeHost(HestiaApiFactory factory, TestClock clock)
    {
        Factory = factory;
        Clock = clock;
    }

    public HestiaApiFactory Factory { get; }
    public TestClock Clock { get; }
    public HttpClient Client => _client ??= Factory.CreateClient();

    public static HomeHost Start(IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null,
        bool signedIn = true, DateTimeOffset? now = null)
    {
        var clock = new TestClock(now ?? Now);
        return new HomeHost(new HestiaApiFactory(Settings(settings), Services(clock, services), signedIn), clock);
    }

    /// <summary>The test settings: the time zone of Home, Tasks, Today, and Alerts, and the Documents OCR off.</summary>
    public static Dictionary<string, string?> Settings(IReadOnlyDictionary<string, string?>? more = null)
    {
        var all = new Dictionary<string, string?>
        {
            ["Home:TimeZone"] = TimeZone,
            ["Tasks:TimeZone"] = TimeZone,
            ["Today:TimeZone"] = TimeZone,
            ["Alerts:TimeZone"] = TimeZone,
            ["Documents:Ocr:Enabled"] = "false"
        };
        foreach (var (key, value) in more ?? new Dictionary<string, string?>())
        {
            all[key] = value;
        }
        return all;
    }

    public static Action<IServiceCollection> Services(TimeProvider clock, Action<IServiceCollection>? more = null) => services =>
    {
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(clock);
        more?.Invoke(services);
    };

    public Task<T> WithDbAsync<T>(Func<HomeDbContext, Task<T>> action) =>
        Factory.WithScopeAsync(provider => action(provider.GetRequiredService<HomeDbContext>()));

    public async Task<RoomDto> CreateRoomAsync(string name, int? sortOrder = null)
    {
        var response = await Client.PostAsJsonAsync(Rooms, new CreateRoomRequest(name, sortOrder), HestiaApiFactory.Json);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<RoomDto>();
    }

    public async Task<List<RoomDto>> RoomsAsync() => await (await Client.GetAsync(Rooms)).ReadAsync<List<RoomDto>>();

    public async Task<ItemDto> CreateAsync(CreateItemRequest request)
    {
        var response = await Client.PostAsJsonAsync(Items, request, HestiaApiFactory.Json);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<ItemDto>();
    }

    public Task<ItemDto> CreateAsync(string name, Guid? roomId = null, decimal? price = null, DateOnly? warrantyUntil = null) =>
        CreateAsync(new CreateItemRequest(name, RoomId: roomId, PurchasePrice: price, WarrantyUntil: warrantyUntil));

    public async Task<ItemDto> GetAsync(Guid id)
    {
        var response = await Client.GetAsync($"{Items}/{id}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<ItemDto>();
    }

    public async Task<PaginatedList<ItemSummaryDto>> ListAsync(string query = "")
    {
        var response = await Client.GetAsync($"{Items}?{query}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<PaginatedList<ItemSummaryDto>>();
    }

    public async Task<HomeSummaryDto> SummaryAsync() => await (await Client.GetAsync($"{Home}/summary")).ReadAsync<HomeSummaryDto>();

    /// <summary>Uploads a small document (a PNG header is enough: the OCR is off) and returns it.</summary>
    public async Task<DocumentDto> UploadDocumentAsync(string title, DateOnly? expiresOn = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(title), "title");
        if (expiresOn is { } day)
        {
            form.Add(new StringContent(day.ToString("yyyy-MM-dd")), "expiresOn");
        }
        // New content for each document (the bytes after the header differ).
        var file = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Guid.NewGuid().ToByteArray()]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "files", "receipt.png");
        var response = await Client.PostAsync("/api/documents", form);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<DocumentDto>();
    }

    /// <summary>Delivers one event to its consumers, as the outbox sender does.</summary>
    public async Task DeliverAsync(object message)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(message);
    }

    public static async Task<HttpValidationProblemDetails> AssertBadRequestAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadAsync<HttpValidationProblemDetails>();
        Assert.True(problem.Errors.ContainsKey(field), $"No error for {field}: {string.Join(", ", problem.Errors.Keys)}");
        return problem;
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await Factory.DisposeAsync();
    }
}

/// <summary>Small helpers for the tests.</summary>
public static class Wait
{
    /// <summary>Reads until <paramref name="done"/> is true, or 15 seconds passed. Returns the last value.</summary>
    public static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var value = await read();
            if (done(value) || DateTimeOffset.UtcNow > deadline)
            {
                return value;
            }
            await Task.Delay(50);
        }
    }
}
