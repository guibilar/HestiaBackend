using Testcontainers.PostgreSql;

namespace Hestia.Testing;

/// <summary>
/// Picks the database server for the whole test run from HESTIA_TEST_POSTGRES:
/// unset — EF Core's in-memory provider (fast, the default);
/// "container" — a PostgreSQL started in Docker with Testcontainers, removed after the run;
/// anything else — the connection string of a PostgreSQL server that you run yourself.
/// Each <see cref="HestiaApiFactory"/> still gets its own database on the server.
/// A test project starts and stops it from an assembly fixture.
/// </summary>
public static class TestDatabase
{
    public const string Variable = "HESTIA_TEST_POSTGRES";
    public const string ContainerSetting = "container";

    // Same major version as docker-compose.yml.
    private const string Image = "postgres:17-alpine";

    private static PostgreSqlContainer? _container;

    /// <summary>PostgreSQL server connection string, or null for the in-memory provider.</summary>
    public static string? PostgresServer { get; private set; }

    public static bool OnPostgres => PostgresServer is not null;

    public static async ValueTask StartAsync()
    {
        var setting = Environment.GetEnvironmentVariable(Variable);
        if (string.Equals(setting, ContainerSetting, StringComparison.OrdinalIgnoreCase))
        {
            _container = new PostgreSqlBuilder(Image).Build();
            await _container.StartAsync();
            PostgresServer = _container.GetConnectionString();
        }
        else
        {
            PostgresServer = string.IsNullOrWhiteSpace(setting) ? null : setting;
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
