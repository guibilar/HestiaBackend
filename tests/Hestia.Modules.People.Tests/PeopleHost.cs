using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.People.Contracts;
using Hestia.Modules.People.Infrastructure;
using Hestia.Platform.Paging;
using Hestia.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;

namespace Hestia.Modules.People.Tests;

/// <summary>A clock that a test sets and moves.</summary>
public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// The host with all modules (<see cref="HestiaApiFactory"/>), with a clock that the test controls: noon in Toronto on
/// October 14th, 2026 (<see cref="Today"/>), and the time zone of the People module set to Toronto.
/// </summary>
public sealed class PeopleHost : IAsyncDisposable
{
    public const string People = "/api/people";
    public const string TimeZone = "America/Toronto";

    /// <summary>Noon in Toronto on October 14th, 2026.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 14, 16, 0, 0, TimeSpan.Zero);

    public static readonly DateOnly Today = new(2026, 10, 14);

    private HttpClient? _client;

    private PeopleHost(HestiaApiFactory factory, TestClock clock)
    {
        Factory = factory;
        Clock = clock;
    }

    public HestiaApiFactory Factory { get; }
    public TestClock Clock { get; }
    public HttpClient Client => _client ??= Factory.CreateClient();

    public static PeopleHost Start(IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null,
        bool signedIn = true, DateTimeOffset? now = null)
    {
        var clock = new TestClock(now ?? Now);
        return new PeopleHost(new HestiaApiFactory(Settings(settings), Services(clock, services), signedIn), clock);
    }

    /// <summary>The test settings: the time zone of all modules, and the Documents OCR off.</summary>
    public static Dictionary<string, string?> Settings(IReadOnlyDictionary<string, string?>? more = null)
    {
        var all = new Dictionary<string, string?>
        {
            ["People:TimeZone"] = TimeZone,
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

    public Task<T> WithDbAsync<T>(Func<PeopleDbContext, Task<T>> action) =>
        Factory.WithScopeAsync(provider => action(provider.GetRequiredService<PeopleDbContext>()));

    public async Task<PersonDto> CreateAsync(CreatePersonRequest request)
    {
        var response = await Client.PostAsJsonAsync(People, request, HestiaApiFactory.Json);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<PersonDto>();
    }

    public Task<PersonDto> CreateAsync(string name, int? contactEveryDays = null, BirthdayDto? birthday = null, params string[] groups) =>
        CreateAsync(new CreatePersonRequest(name, Groups: groups, Birthday: birthday, ContactEveryDays: contactEveryDays));

    public async Task<PersonDto> GetAsync(Guid id)
    {
        var response = await Client.GetAsync($"{People}/{id}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<PersonDto>();
    }

    public async Task<PaginatedList<PersonSummaryDto>> ListAsync(string query = "")
    {
        var response = await Client.GetAsync($"{People}?{query}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<PaginatedList<PersonSummaryDto>>();
    }

    public async Task<PeopleSummaryDto> SummaryAsync() => await (await Client.GetAsync($"{People}/summary")).ReadAsync<PeopleSummaryDto>();

    public async Task<T> PostAsync<T>(string url, object body)
    {
        var response = await Client.PostAsJsonAsync(url, body, HestiaApiFactory.Json);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<T>();
    }

    public async Task<T> PutAsync<T>(string url, object body)
    {
        var response = await Client.PutAsJsonAsync(url, body, HestiaApiFactory.Json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<T>();
    }

    public Task<InteractionDto> LogContactAsync(Guid personId, DateOnly? on = null, string? kind = null, string? note = null) =>
        PostAsync<InteractionDto>($"{People}/{personId}/interactions", new InteractionRequest(on, kind, note));

    public Task<LoanDto> AddLoanAsync(Guid personId, LoanRequest request) => PostAsync<LoanDto>($"{People}/{personId}/loans", request);

    public async Task<IReadOnlyList<AlertDto>> AlertsAsync() =>
        (await (await Client.GetAsync("/api/alerts?refresh=true")).ReadAsync<AlertList>()).Alerts.Where(a => a.Module == "People").ToList();

    /// <summary>Uploads a small document (a PNG header is enough: the OCR is off) and returns it.</summary>
    public async Task<DocumentDto> UploadDocumentAsync(string title)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(title), "title");
        var file = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Guid.NewGuid().ToByteArray()]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "files", "card.png");
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
