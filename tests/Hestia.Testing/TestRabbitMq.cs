using Testcontainers.RabbitMq;

namespace Hestia.Testing;

/// <summary>
/// Picks the RabbitMQ broker for the whole test run from HESTIA_TEST_RABBITMQ:
/// unset — no broker: the tests that need one are skipped;
/// "container" — a RabbitMQ started in Docker with Testcontainers, removed after the run;
/// anything else — the amqp:// address of a broker that you run yourself.
/// A test project starts and stops it from an assembly fixture, with <see cref="TestDatabase"/>.
/// </summary>
public static class TestRabbitMq
{
    public const string Variable = "HESTIA_TEST_RABBITMQ";
    public const string ContainerSetting = "container";

    // The same major version as the finance compose file.
    private const string Image = "rabbitmq:4-alpine";

    private static RabbitMqContainer? _container;

    /// <summary>The broker address (amqp://user:password@host:port), or null when the run has no broker.</summary>
    public static string? Address { get; private set; }

    public static bool Available => Address is not null;

    public static async ValueTask StartAsync()
    {
        var setting = Environment.GetEnvironmentVariable(Variable);
        if (string.Equals(setting, ContainerSetting, StringComparison.OrdinalIgnoreCase))
        {
            _container = new RabbitMqBuilder(Image).Build();
            await _container.StartAsync();
            Address = _container.GetConnectionString();
        }
        else
        {
            Address = string.IsNullOrWhiteSpace(setting) ? null : setting;
        }
    }

    public static async ValueTask StopAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }
}
