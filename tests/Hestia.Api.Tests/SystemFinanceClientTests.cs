using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using Hestia.Api.Gateway;
using Hestia.Modules.Sample.Contracts;
using Hestia.Platform.Finance;
using Hestia.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Hestia.Api.Tests;

/// <summary>
/// The system identity for background work (T42): code with no signed-in user (an event consumer, a timer, a host
/// command) calls finance with <c>IFinanceClient.AsSystem(...)</c>. It is read-only unless the code asks for full
/// access, and it never works inside an HTTP request.
/// </summary>
public class SystemFinanceClientTests
{
    private sealed record FinanceAnswer(string Source);

    /// <summary>The finance client of a host, outside any request (as in a timer or a command).</summary>
    private static (IFinanceClient Client, AsyncServiceScope Scope) BackgroundClient(HestiaApiFactory host)
    {
        _ = host.CreateClient();
        var scope = host.Services.CreateAsyncScope();
        return (scope.ServiceProvider.GetRequiredService<IFinanceClient>(), scope);
    }

    [Fact]
    public async Task AsSystem_IsReadOnlyByDefault_WithTheSystemIdentityAndTheCallerName()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = new HestiaApiFactory(finance.HostSettings());
        var (client, scope) = BackgroundClient(host);
        await using var _ = scope;

        var answer = await client.AsSystem("Tests.Timer").GetAsync<FinanceAnswer>("/api/Accounts");

        Assert.Equal("finance", answer.Source);
        var token = GatewayToken.Read(Assert.Single(finance.Requests));
        Assert.Equal((GatewayTokens.SystemSubject, "system:Tests.Timer", "read-only"), (token.Subject, token.Name, token.Scope));
        Assert.Equal(TimeSpan.FromSeconds(60), token.Lifetime);
    }

    [Fact]
    public async Task AsSystem_WithFullAccess_HasTheFullScope()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = new HestiaApiFactory(finance.HostSettings());
        var (client, scope) = BackgroundClient(host);
        await using var _ = scope;

        await client.AsSystem("Tests.Writer", FinanceAccess.Full)
            .PostAsync<object, FinanceAnswer>("/api/Transaction", new { Description = "Coffee" });

        Assert.Equal("full", GatewayToken.Read(Assert.Single(finance.Requests)).Scope);
    }

    [Fact]
    public async Task TheClientWithoutAsSystem_StillRefusesBackgroundCode()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = new HestiaApiFactory(finance.HostSettings());
        var (client, scope) = BackgroundClient(host);
        await using var _ = scope;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync<FinanceAnswer>("/api/Accounts"));
        Assert.Empty(finance.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("name\nwith-a-new-line")]
    public async Task AsSystem_RefusesAnInvalidCallerName(string caller)
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = new HestiaApiFactory(finance.HostSettings());
        var (client, scope) = BackgroundClient(host);
        await using var _ = scope;

        Assert.Throws<ArgumentException>(() => client.AsSystem(caller));
        Assert.Throws<ArgumentException>(() => client.AsSystem(new string('a', 65)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InsideAnHttpRequest_AsSystemIsRefused_WithOrWithoutAUser(bool signedIn)
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = new HestiaApiFactory(finance.HostSettings());
        _ = host.CreateClient();
        await using var scope = host.Services.CreateAsyncScope();
        var user = signedIn
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "u1"), new Claim(ClaimTypes.Name, "alex")], "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext { User = user };
        var client = scope.ServiceProvider.GetRequiredService<IFinanceClient>();

        Assert.Throws<InvalidOperationException>(() => client.AsSystem("Tests.Endpoint"));
        Assert.Empty(finance.Requests);
    }

    [Fact]
    public async Task ASystemClientKeptFromBackgroundCode_IsRefusedInsideAnHttpRequest()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = new HestiaApiFactory(finance.HostSettings());
        var (client, scope) = BackgroundClient(host);
        await using var _ = scope;
        var system = client.AsSystem("Tests.Kept");

        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() => system.GetAsync<FinanceAnswer>("/api/Accounts"));
        Assert.Empty(finance.Requests);
    }

    [Fact]
    public async Task AModuleEventConsumer_CallsFinanceAsTheSystem_AfterAnHttpRequestRaisedTheEvent()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = new HestiaApiFactory(finance.HostSettings(), services =>
        {
            services.AddSingleton<FinanceCallingConsumerResults>();
            services.AddSingleton<IWolverineExtension, IncludeFinanceCallingConsumer>();
        });
        var http = host.CreateClient();

        // The signed-in request creates an item. The module event reaches the consumer after the commit.
        var created = await http.PostAsJsonAsync("/api/sample/items", new CreateSampleItemRequest("Kettle"));
        created.EnsureSuccessStatusCode();

        var result = await host.Services.GetRequiredService<FinanceCallingConsumerResults>().WaitAsync("Kettle");
        Assert.Null(result.Error);
        Assert.Equal("finance", result.Source);
        var received = Assert.Single(finance.Requests, request => request.Target == "/api/Accounts");
        var token = GatewayToken.Read(received);
        Assert.Equal((GatewayTokens.SystemSubject, "system:Tests.FinanceCallingConsumer", "read-only"),
            (token.Subject, token.Name, token.Scope));
    }

    [Fact]
    public async Task EachSystemCall_IsLoggedAtDebugLevel_WithTheCallerName()
    {
        await using var finance = await FakeFinance.StartAsync();
        var logs = new CapturedLogs();
        await using var host = new HestiaApiFactory(
            finance.HostSettings(new Dictionary<string, string?> { ["Logging:LogLevel:Hestia.Api.Gateway.FinanceClient"] = "Debug" }),
            services => services.AddSingleton<ILoggerProvider>(logs));
        var (client, scope) = BackgroundClient(host);
        await using var _ = scope;

        await client.AsSystem("Tests.Logged").GetAsync<FinanceAnswer>("/api/Accounts");

        var entry = Assert.Single(logs.Entries, entry => entry.Message.Contains("Tests.Logged", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("/api/Accounts", entry.Message, StringComparison.Ordinal);
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) =>
            categoryName == "Hestia.Api.Gateway.FinanceClient" ? new Logger(this) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose()
        {
        }

        private sealed class Logger(CapturedLogs logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                logs.Entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>A test consumer: it reads finance as the system identity when an item is created.</summary>
public sealed class FinanceCallingConsumer(IFinanceClient finance, FinanceCallingConsumerResults results)
{
    public async Task Consume(SampleItemCreated message)
    {
        try
        {
            var answer = await finance.AsSystem("Tests.FinanceCallingConsumer").GetAsync<Dictionary<string, string>>("/api/Accounts");
            results.Add(message.Name, new FinanceCallingConsumerResult(answer.GetValueOrDefault("source"), null));
        }
        catch (Exception exception)
        {
            // Recorded, not thrown: a retry would hide the first error.
            results.Add(message.Name, new FinanceCallingConsumerResult(null, exception.Message));
        }
    }
}

public sealed record FinanceCallingConsumerResult(string? Source, string? Error);

public sealed class FinanceCallingConsumerResults
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<FinanceCallingConsumerResult>> _results = new();

    public void Add(string itemName, FinanceCallingConsumerResult result) => Get(itemName).TrySetResult(result);

    public Task<FinanceCallingConsumerResult> WaitAsync(string itemName) => Get(itemName).Task.WaitAsync(TimeSpan.FromSeconds(15));

    private TaskCompletionSource<FinanceCallingConsumerResult> Get(string itemName) =>
        _results.GetOrAdd(itemName, _ => new TaskCompletionSource<FinanceCallingConsumerResult>(TaskCreationOptions.RunContinuationsAsynchronously));
}

/// <summary>Adds <see cref="FinanceCallingConsumer"/> to the Wolverine handlers of one test host.</summary>
public sealed class IncludeFinanceCallingConsumer : IWolverineExtension
{
    public void Configure(WolverineOptions options) => options.Discovery.IncludeType(typeof(FinanceCallingConsumer));
}
