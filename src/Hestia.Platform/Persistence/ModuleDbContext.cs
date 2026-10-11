using Microsoft.EntityFrameworkCore;

namespace Hestia.Platform.Persistence;

/// <summary>
/// The base class for a module's DbContext. All its tables are in the module's own schema
/// (the lower-case module name). No module reads another module's tables.
/// </summary>
public abstract class ModuleDbContext(DbContextOptions options, string schema) : DbContext(options)
{
    public string Schema { get; } = schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        base.OnModelCreating(modelBuilder);
    }
}
