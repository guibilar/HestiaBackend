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
    /// <param name="services">The services.</param>
    /// <param name="schema">The module's schema: the lower-case module name.</param>
    /// <param name="configure">Optional: for example <c>database => database.UseDurableEvents()</c> (T38).</param>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema,
        Action<ModuleDatabaseOptions>? configure = null)
        where TContext : ModuleDbContext
    {
        var options = new ModuleDatabaseOptions();
        configure?.Invoke(options);

        services.TryAddSingleton<IDatabaseOptionsConfigurator, NpgsqlDatabaseOptionsConfigurator>();
        services.TryAddScoped<IEventPublisher, MissingEventPublisher>();
        services.TryAddSingleton<IModuleEventSerializer, MissingModuleEventSerializer>();
        services.TryAddSingleton<ModuleOutboxSignal>();
        services.TryAddScoped<ModuleEventsInterceptor>();
        services.TryAddScoped<ModuleEventsTransactionInterceptor>();
        // The options are made per scope (the default), so the interceptors are the scope's, with its publisher.
        services.AddDbContext<TContext>((provider, builder) =>
        {
            provider.GetRequiredService<IDatabaseOptionsConfigurator>().Configure(builder, schema);
            builder.AddInterceptors(
                provider.GetRequiredService<ModuleEventsInterceptor>(),
                provider.GetRequiredService<ModuleEventsTransactionInterceptor>());
        });
        services.AddSingleton(new ModuleDatabase(typeof(TContext), schema, options.DurableEvents));
        return services;
    }
}
