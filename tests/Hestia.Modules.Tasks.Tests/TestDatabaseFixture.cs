using Hestia.Testing;

[assembly: AssemblyFixture(typeof(Hestia.Modules.Tasks.Tests.TestDatabaseFixture))]

namespace Hestia.Modules.Tasks.Tests;

/// <summary>Starts the test database server once for this test assembly (see <see cref="TestDatabase"/>).</summary>
public sealed class TestDatabaseFixture : IAsyncLifetime
{
    public ValueTask InitializeAsync() => TestDatabase.StartAsync();

    public ValueTask DisposeAsync() => TestDatabase.StopAsync();
}
