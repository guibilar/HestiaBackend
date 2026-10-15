using Hestia.Testing;

namespace Hestia.Api.Tests;

/// <summary>
/// The tests that only read the handlers of a started host with the default settings (<see cref="HandlerCompileTests"/>,
/// T47, and <see cref="ConsumerInboxTests"/>, T52). They share one host (T76): each host creates and migrates its own
/// database, so one host saves that time in CI.
/// </summary>
[CollectionDefinition(Name)]
public sealed class StartedHostCollection : ICollectionFixture<StartedHost>
{
    public const string Name = "Started host";
}

/// <summary>One started host with the default settings. The tests must not change its data.</summary>
public sealed class StartedHost : IAsyncLifetime
{
    private readonly HestiaApiFactory _factory = new();

    public IServiceProvider Services => _factory.Services;

    public ValueTask InitializeAsync()
    {
        _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
