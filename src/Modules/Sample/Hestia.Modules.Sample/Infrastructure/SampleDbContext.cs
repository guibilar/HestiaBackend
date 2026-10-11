using Hestia.Modules.Sample.Domain;
using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hestia.Modules.Sample.Infrastructure;

public class SampleDbContext(DbContextOptions<SampleDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "sample";

    public DbSet<SampleItem> Items => Set<SampleItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SampleItem>(item =>
        {
            item.ToTable("Items");
            item.HasKey(i => i.Id);
            item.Property(i => i.Name).HasMaxLength(SampleItem.MaxNameLength).IsRequired();
            item.HasIndex(i => i.Name).IsUnique();
        });
    }
}

/// <summary>For `dotnet ef` only: the migrations tools make the context with this factory.</summary>
public class SampleDbContextDesignTimeFactory : IDesignTimeDbContextFactory<SampleDbContext>
{
    public SampleDbContext CreateDbContext(string[] args) =>
        new(HestiaDatabase.DesignTimeOptions<SampleDbContext>(SampleDbContext.SchemaName));
}
