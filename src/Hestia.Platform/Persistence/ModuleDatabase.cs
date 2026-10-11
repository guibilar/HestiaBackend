namespace Hestia.Platform.Persistence;

/// <summary>A module's DbContext and its schema, registered by <see cref="PersistenceServiceCollectionExtensions.AddModuleDbContext{TContext}"/>.</summary>
public sealed record ModuleDatabase(Type ContextType, string Schema);
