using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hestia.Platform.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers a module's DbContext on the Hestia database, in its own schema. The host applies its
    /// migrations on start (Database:MigrateOnStartup) and checks it in /health/ready.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema)
        where TContext : ModuleDbContext
    {
        services.TryAddSingleton<IDatabaseOptionsConfigurator, NpgsqlDatabaseOptionsConfigurator>();
        services.AddDbContext<TContext>((provider, options) =>
            provider.GetRequiredService<IDatabaseOptionsConfigurator>().Configure(options, schema));
        services.AddSingleton(new ModuleDatabase(typeof(TContext), schema));
        return services;
    }
}
