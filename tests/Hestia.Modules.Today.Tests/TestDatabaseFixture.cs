using Hestia.Testing;

[assembly: AssemblyFixture(typeof(Hestia.Modules.Today.Tests.TestDatabaseFixture))]

namespace Hestia.Modules.Today.Tests;

/// <summary>Starts the test database server once for this test assembly (see <see cref="TestDatabase"/>).</summary>
public sealed class TestDatabaseFixture : IAsyncLifetime
{
    public ValueTask InitializeAsync() => TestDatabase.StartAsync();

    public ValueTask DisposeAsync() => TestDatabase.StopAsync();
}
