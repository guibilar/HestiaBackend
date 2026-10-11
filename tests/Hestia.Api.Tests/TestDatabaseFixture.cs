using Hestia.Testing;

[assembly: AssemblyFixture(typeof(Hestia.Api.Tests.TestDatabaseFixture))]

namespace Hestia.Api.Tests;

/// <summary>
/// Starts the test database server and the RabbitMQ broker once for this test assembly
/// (see <see cref="TestDatabase"/> and <see cref="TestRabbitMq"/>).
/// </summary>
public sealed class TestDatabaseFixture : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await TestDatabase.StartAsync();
        await TestRabbitMq.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await TestRabbitMq.StopAsync();
        await TestDatabase.StopAsync();
    }
}
