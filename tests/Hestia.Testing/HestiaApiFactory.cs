using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hestia.Platform.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Hestia.Testing;

/// <summary>
/// Runs the real host, with all modules, against a private database: in-memory by default, or a database
/// on the PostgreSQL server that <see cref="TestDatabase"/> picked, migrated by the host on start (as in Docker).
/// </summary>
public class HestiaApiFactory(
    IReadOnlyDictionary<string, string?>? settings = null,
    Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"hestia_test_{Guid.NewGuid():N}";
    private readonly InMemoryDatabaseRoot _inMemoryRoot = new();
    private bool _createdDatabase;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (TestDatabase.PostgresServer is { } server)
        {
            builder.UseSetting($"ConnectionStrings:{HestiaDatabase.ConnectionStringName}",
                new NpgsqlConnectionStringBuilder(server) { Database = _databaseName }.ConnectionString);
            builder.UseSetting("Database:MigrateOnStartup", "true");
        }
        else
        {
            // The in-memory provider has no migrations.
            builder.UseSetting("Database:MigrateOnStartup", "false");
        }
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            if (TestDatabase.PostgresServer is null)
            {
                services.RemoveAll<IDatabaseOptionsConfigurator>();
                services.AddSingleton<IDatabaseOptionsConfigurator>(
                    new InMemoryDatabaseOptionsConfigurator(_databaseName, _inMemoryRoot));
            }
            configureServices?.Invoke(services);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        _createdDatabase = TestDatabase.PostgresServer is not null;
        return host;
    }

    // Dispose() also ends up here. The database is dropped after the host stops.
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        if (_createdDatabase && TestDatabase.PostgresServer is { } server)
        {
            _createdDatabase = false;
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(server);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Runs <paramref name="action"/> with a service scope of the host, for example to read a DbContext.</summary>
    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private sealed class InMemoryDatabaseOptionsConfigurator(string databaseName, InMemoryDatabaseRoot root)
        : IDatabaseOptionsConfigurator
    {
        public void Configure(DbContextOptionsBuilder options, string schema) =>
            options.UseInMemoryDatabase(databaseName, root);
    }
}

public static class HttpResponseExtensions
{
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(HestiaApiFactory.Json))!;
}
