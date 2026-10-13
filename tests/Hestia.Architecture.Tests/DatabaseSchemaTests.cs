using System.Reflection;
using Hestia.Platform.Events;
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

    [Fact]
    public void EveryModuleOutbox_IsInTheSchemaOfItsModule()
    {
        // T38: a durable module's events are rows in its own schema. The sender reads them with the module's own
        // DbContext, so no module reads the outbox of another module.
        var outboxes = Solution.ModuleAssemblies()
            .SelectMany(assembly => ContextTypes(assembly).Select(type => (Module: Solution.ModuleNameOf(assembly), Type: type)))
            .Select(context =>
            {
                using var db = Create(context.Type);
                var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(OutboxMessage));
                return (context.Module, Schema: entity?.GetSchema(), Table: entity?.GetTableName());
            })
            .Where(outbox => outbox.Table is not null)
            .ToList();

        Assert.Contains(outboxes, outbox => outbox.Module == "Sample");
        Assert.All(outboxes, outbox =>
        {
            Assert.Equal(outbox.Module.ToLowerInvariant(), outbox.Schema);
            Assert.Equal(EventOutboxModelBuilderExtensions.TableName, outbox.Table);
        });
    }

    [Fact]
    public void NoModuleCode_UsesOutboxRows()
    {
        // The platform writes the outbox rows and the host's sender reads them. A module only raises events, so it can
        // never read another module's outbox (or its own). Migrations are generated code and name the entity type.
        var root = Path.Combine(Solution.RootDirectory(), "src", "Modules");
        var separator = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}Migrations{separator}", StringComparison.Ordinal) &&
                           !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal) &&
                           !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(files);
        Assert.All(files, path => Assert.False(
            File.ReadAllText(path).Contains(nameof(OutboxMessage), StringComparison.Ordinal),
            $"{Path.GetRelativePath(root, path)} uses {nameof(OutboxMessage)}. Raise events with RaiseEvent instead."));
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
