using Hestia.Modules.Sample.Domain;
using Hestia.Platform.Events;
using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hestia.Modules.Sample.Infrastructure;

public class SampleDbContext(DbContextOptions<SampleDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "sample";

    public DbSet<SampleItem> Items => Set<SampleItem>();

    /// <summary>What the Sample consumer handled (SampleItemCreatedConsumer).</summary>
    public DbSet<SampleActivityEntry> Activity => Set<SampleActivityEntry>();

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

        modelBuilder.Entity<SampleActivityEntry>(entry =>
        {
            entry.ToTable("Activity");
            entry.HasKey(e => e.Id);
            entry.Property(e => e.Name).HasMaxLength(SampleItem.MaxNameLength).IsRequired();
        });

        // The inbox of the module's idempotent consumers (T36): the table "EventInbox" in this schema.
        modelBuilder.AddEventInbox();
    }
}

/// <summary>For `dotnet ef` only: the migrations tools make the context with this factory.</summary>
public class SampleDbContextDesignTimeFactory : IDesignTimeDbContextFactory<SampleDbContext>
{
    public SampleDbContext CreateDbContext(string[] args) =>
        new(HestiaDatabase.DesignTimeOptions<SampleDbContext>(SampleDbContext.SchemaName));
}
