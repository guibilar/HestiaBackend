using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Hestia.Platform.Persistence;

/// <summary>The Hestia database: one PostgreSQL database, one schema per module (D8).</summary>
public static class HestiaDatabase
{
    public const string ConnectionStringName = "Hestia";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>Used only by `dotnet ef` at design time. It never connects.</summary>
    private const string DesignTimeConnectionString = "Host=localhost;Database=Hestia;Username=postgres;Password=postgres";

    public static string GetConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString(ConnectionStringName)
        ?? throw new InvalidOperationException(
            $"ConnectionStrings:{ConnectionStringName} is not set. Outside Development, set it with the " +
            $"ConnectionStrings__{ConnectionStringName} environment variable.");

    /// <summary>Each module keeps its migration history in its own schema, so modules migrate independently.</summary>
    public static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql, string schema)
    {
        npgsql.MigrationsHistoryTable(MigrationsHistoryTable, schema);
        // A document with its files and its links (and the same in other modules) in one SQL query repeats each file
        // once per link. Split queries load each collection on its own, and EF stops warning about it on every request.
        npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
        npgsql.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorCodesToAdd: null);
    }

    /// <summary>Options for a module's design-time DbContext factory (`dotnet ef`).</summary>
    public static DbContextOptions<TContext> DesignTimeOptions<TContext>(string schema) where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(DesignTimeConnectionString, npgsql => ConfigureNpgsql(npgsql, schema))
            .Options;

    /// <summary>Applies the migrations of every module, one module after the other.</summary>
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        foreach (var database in scope.ServiceProvider.GetServices<ModuleDatabase>())
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(database.ContextType);
            await context.Database.MigrateAsync(cancellationToken);
        }
    }
}
