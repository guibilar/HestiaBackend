using Hestia.Platform.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hestia.Platform.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers a module's DbContext on the Hestia database, in its own schema. The host applies its
    /// migrations on start (Database:MigrateOnStartup) and checks it in /health/ready.
    /// The context publishes its module events after commit (see <see cref="ModuleDbContext.RaiseEvent"/>).
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema)
        where TContext : ModuleDbContext
    {
        services.TryAddSingleton<IDatabaseOptionsConfigurator, NpgsqlDatabaseOptionsConfigurator>();
        services.TryAddScoped<IEventPublisher, MissingEventPublisher>();
        services.TryAddScoped<ModuleEventsInterceptor>();
        services.TryAddScoped<ModuleEventsTransactionInterceptor>();
        // The options are made per scope (the default), so the interceptors are the scope's, with its publisher.
        services.AddDbContext<TContext>((provider, options) =>
        {
            provider.GetRequiredService<IDatabaseOptionsConfigurator>().Configure(options, schema);
            options.AddInterceptors(
                provider.GetRequiredService<ModuleEventsInterceptor>(),
                provider.GetRequiredService<ModuleEventsTransactionInterceptor>());
        });
        services.AddSingleton(new ModuleDatabase(typeof(TContext), schema));
        return services;
    }
}
