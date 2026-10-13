namespace Hestia.Platform.Persistence;

/// <summary>
/// A module's DbContext and its schema, registered by <see cref="PersistenceServiceCollectionExtensions.AddModuleDbContext{TContext}"/>.
/// </summary>
/// <param name="ContextType">The module's DbContext type.</param>
/// <param name="Schema">The module's schema.</param>
/// <param name="DurableEvents">True when the module stores its events in its outbox (T38, <see cref="ModuleDatabaseOptions.UseDurableEvents"/>).</param>
public sealed record ModuleDatabase(Type ContextType, string Schema, bool DurableEvents = false);

/// <summary>The options of a module's database (<see cref="PersistenceServiceCollectionExtensions.AddModuleDbContext{TContext}"/>).</summary>
public sealed class ModuleDatabaseOptions
{
    /// <summary>True when the module's events go through its outbox. See <see cref="UseDurableEvents"/>.</summary>
    public bool DurableEvents { get; private set; }

    /// <summary>
    /// Durable module events (T38). Each event that the module raises is stored in its outbox (the table
    /// <c>EventOutbox</c> in its schema) in the same transaction as the change. The host's outbox sender then delivers it
    /// to the consumers, also after a restart. Without this call, the events go to the consumers in memory after the
    /// commit, and a process stop between the commit and the publish loses them (the default).
    /// </summary>
    /// <remarks>The module's model needs the outbox: call <c>modelBuilder.AddEventOutbox()</c> in <c>OnModelCreating</c>.</remarks>
    /// <param name="enabled">False keeps the in-memory path, for example from a setting.</param>
    public ModuleDatabaseOptions UseDurableEvents(bool enabled = true)
    {
        DurableEvents = enabled;
        return this;
    }
}
