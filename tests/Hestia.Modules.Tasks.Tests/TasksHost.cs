using System.Net;
using System.Net.Http.Json;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Modules.Tasks.Infrastructure;
using Hestia.Platform.Paging;
using Hestia.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hestia.Modules.Tasks.Tests;

/// <summary>A clock that a test sets and moves.</summary>
public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// The host with all modules (<see cref="HestiaApiFactory"/>), with a clock that the test controls: noon in Toronto on
/// October 14th, 2026 (<see cref="Today"/>), and the time zone of the Tasks module set to Toronto.
/// </summary>
public sealed class TasksHost : IAsyncDisposable
{
    public const string Tasks = "/api/tasks";
    public const string TimeZone = "America/Toronto";

    /// <summary>Noon in Toronto on October 14th, 2026.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 14, 16, 0, 0, TimeSpan.Zero);

    public static readonly DateOnly Today = new(2026, 10, 14);

    private HttpClient? _client;

    private TasksHost(HestiaApiFactory factory, TestClock clock)
    {
        Factory = factory;
        Clock = clock;
    }

    public HestiaApiFactory Factory { get; }
    public TestClock Clock { get; }
    public HttpClient Client => _client ??= Factory.CreateClient();

    public static TasksHost Start(IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null,
        bool signedIn = true, DateTimeOffset? now = null)
    {
        var clock = new TestClock(now ?? Now);
        return new TasksHost(new HestiaApiFactory(Settings(settings), Services(clock, services), signedIn), clock);
    }

    /// <summary>The test settings: the time zone of Tasks, Today, and Alerts, and the Documents OCR off.</summary>
    public static Dictionary<string, string?> Settings(IReadOnlyDictionary<string, string?>? more = null)
    {
        var all = new Dictionary<string, string?>
        {
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

    public Task<T> WithDbAsync<T>(Func<TasksDbContext, Task<T>> action) =>
        Factory.WithScopeAsync(provider => action(provider.GetRequiredService<TasksDbContext>()));

    public async Task<TaskDto> CreateAsync(CreateTaskRequest request)
    {
        var response = await Client.PostAsJsonAsync(Tasks, request, HestiaApiFactory.Json);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<TaskDto>();
    }

    public Task<TaskDto> CreateAsync(string title, DateOnly? dueOn = null, TaskRepeatRequest? repeat = null, IReadOnlyList<string>? tags = null) =>
        CreateAsync(new CreateTaskRequest(title, DueOn: dueOn, Tags: tags, Repeat: repeat));

    public async Task<TaskDto> GetAsync(Guid id) => await (await Client.GetAsync($"{Tasks}/{id}")).ReadAsync<TaskDto>();

    public async Task<TaskDto> DoneAsync(Guid id)
    {
        var response = await Client.PostAsync($"{Tasks}/{id}/done", null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<TaskDto>();
    }

    public async Task<TaskDto> UndoAsync(Guid id)
    {
        var response = await Client.PostAsync($"{Tasks}/{id}/undo", null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<TaskDto>();
    }

    public async Task<PaginatedList<TaskSummaryDto>> ListAsync(string query = "")
    {
        var response = await Client.GetAsync($"{Tasks}?{query}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<PaginatedList<TaskSummaryDto>>();
    }

    public static async Task<HttpValidationProblemDetails> AssertBadRequestAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadAsync<HttpValidationProblemDetails>();
        Assert.True(problem.Errors.ContainsKey(field), $"No error for {field}: {string.Join(", ", problem.Errors.Keys)}");
        return problem;
    }

    public static TaskRepeatRequest Every(int every, RepeatUnit unit, RepeatMode mode = RepeatMode.FromDueDate) => new(every, unit, mode);

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
