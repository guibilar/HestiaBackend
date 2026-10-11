using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hestia.Api.Events;
using Hestia.Platform.Modules;
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
using Wolverine;

namespace Hestia.Testing;

/// <summary>
/// Runs the real host, with all modules, against a private database: in-memory by default, or a database
/// on the PostgreSQL server that <see cref="TestDatabase"/> picked, migrated by the host on start (as in Docker).
/// Requests are signed in as <see cref="TestSignIn"/> unless <paramref name="signedIn"/> is false: then the real
/// sign-in of the Identity module is in charge. The gateway signing key is <see cref="TestGatewayKey"/>.
/// </summary>
public class HestiaApiFactory(
    IReadOnlyDictionary<string, string?>? settings = null,
    Action<IServiceCollection>? configureServices = null,
    bool signedIn = true) : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"hestia_test_{Guid.NewGuid():N}";
    private readonly InMemoryDatabaseRoot _inMemoryRoot = new();
    private bool _createdDatabase;

    /// <summary>The events that reached the host's consumers (all event types are recorded).</summary>
    public EventRecorder Events => Services.GetRequiredService<EventRecorder>();

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
        builder.UseSetting("FinanceProxy:SigningKey", TestGatewayKey.SigningKey);
        // The test host runs in Development: no development account (a test makes the accounts that it needs).
        builder.UseSetting("Identity:InitialUser:Username", "");
        builder.UseSetting("Identity:InitialUser:Password", "");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<EventRecorder>();
            services.AddSingleton<IWolverineExtension>(new RecordEvents(HestiaEvents.EventTypes(ModuleLoader.Discover())));
            if (TestDatabase.PostgresServer is null)
            {
                services.RemoveAll<IDatabaseOptionsConfigurator>();
                services.AddSingleton<IDatabaseOptionsConfigurator>(
                    new InMemoryDatabaseOptionsConfigurator(_databaseName, _inMemoryRoot));
            }
            if (signedIn)
            {
                TestSignIn.Register(services);
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
