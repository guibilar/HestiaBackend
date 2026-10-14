using Hestia.Modules.Alerts.Domain;
using Hestia.Platform.Events;
using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hestia.Modules.Alerts.Infrastructure;

public class AlertsDbContext(DbContextOptions<AlertsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "alerts";

    public DbSet<Alert> Alerts => Set<Alert>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Alert>(alert =>
        {
            alert.ToTable("Alerts");
            alert.HasKey(a => a.Id);
            alert.Property(a => a.Source).HasMaxLength(Alert.MaxSourceLength).IsRequired();
            alert.Property(a => a.Module).HasMaxLength(Alert.MaxModuleLength).IsRequired();
            alert.Property(a => a.Key).HasMaxLength(Alert.MaxKeyLength).IsRequired();
            alert.Property(a => a.Kind).HasMaxLength(Alert.MaxKindLength).IsRequired();
            alert.Property(a => a.Severity).HasConversion<string>().HasMaxLength(20);
            alert.Property(a => a.Title).HasMaxLength(Alert.MaxTitleLength).IsRequired();
            alert.Property(a => a.Text).HasMaxLength(Alert.MaxTextLength).IsRequired();
            alert.Property(a => a.Values).IsRequired();
            alert.Property(a => a.Link).HasMaxLength(Alert.MaxLinkLength);
            alert.Property(a => a.SortKey).HasMaxLength(Alert.MaxSortKeyLength).IsRequired();

            // One open alert per condition. Resolved alerts stay for a time (Alerts:KeepResolvedDays), with the same key.
            alert.HasIndex(a => a.Key).IsUnique().HasFilter("\"ResolvedAt\" IS NULL");
            // A check reads the open alerts of its source.
            alert.HasIndex(a => new { a.Source, a.ResolvedAt });
        });

        // The inbox of the consumers that change alerts (T36): the table "EventInbox" in this schema.
        modelBuilder.AddEventInbox();
    }
}

/// <summary>For `dotnet ef` only: the migrations tools make the context with this factory.</summary>
public class AlertsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AlertsDbContext>
{
    public AlertsDbContext CreateDbContext(string[] args) =>
        new(HestiaDatabase.DesignTimeOptions<AlertsDbContext>(AlertsDbContext.SchemaName));
}
