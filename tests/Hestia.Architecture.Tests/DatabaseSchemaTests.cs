using System.Reflection;
using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Hestia.Architecture.Tests;

/// <summary>Each module's DbContext uses its own schema (D6, D8).</summary>
public class DatabaseSchemaTests
{
    [Fact]
    public void EveryDbContextInAModule_IsAModuleDbContext()
    {
        var contexts = Solution.ModuleAssemblies().SelectMany(ContextTypes).ToList();

        Assert.NotEmpty(contexts);
        Assert.All(contexts, type => Assert.True(typeof(ModuleDbContext).IsAssignableFrom(type),
            $"{type.FullName} must derive from {nameof(ModuleDbContext)}."));
    }

    [Fact]
    public void EveryModuleDbContext_PutsAllTablesInTheSchemaOfItsModule()
    {
        Assert.All(Solution.ModuleAssemblies(), assembly =>
        {
            var expectedSchema = Solution.ModuleNameOf(assembly).ToLowerInvariant();
            foreach (var type in ContextTypes(assembly))
            {
                using var context = Create(type);
                Assert.Equal(expectedSchema, context.Schema);

                var model = context.GetService<IDesignTimeModel>().Model;
                Assert.All(model.GetEntityTypes().Where(entity => entity.GetTableName() is not null), entity =>
                    Assert.Equal(expectedSchema, entity.GetSchema()));
            }
        });
    }

    [Fact]
    public void NoTwoModules_ShareASchema()
    {
        var schemas = Solution.ModuleAssemblies()
            .SelectMany(ContextTypes)
            .Select(type =>
            {
                using var context = Create(type);
                return context.Schema;
            })
            .ToList();

        Assert.Equal(schemas.Count, schemas.Distinct(StringComparer.Ordinal).Count());
    }

    private static IEnumerable<Type> ContextTypes(Assembly assembly) =>
        assembly.GetTypes().Where(type => type is { IsClass: true, IsAbstract: false } && typeof(DbContext).IsAssignableFrom(type));

    /// <summary>A context on PostgreSQL options. Reading the model never connects.</summary>
    private static ModuleDbContext Create(Type contextType)
    {
        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType))!;
        builder.UseNpgsql("Host=localhost;Database=Hestia");
        return (ModuleDbContext)Activator.CreateInstance(contextType, builder.Options)!;
    }
}
