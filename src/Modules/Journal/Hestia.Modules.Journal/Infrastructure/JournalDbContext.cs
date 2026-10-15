using Hestia.Modules.Journal.Domain;
using Hestia.Platform.Events;
using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hestia.Modules.Journal.Infrastructure;

public class JournalDbContext(DbContextOptions<JournalDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "journal";

    /// <summary>The full-text index of an entry: the text, in the "english" and the "simple" configurations.</summary>
    public const string SearchVectorSql =
        "to_tsvector('english'::regconfig, \"Text\") || to_tsvector('simple'::regconfig, \"Text\")";

    public DbSet<JournalEntry> Entries => Set<JournalEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<JournalEntry>(entry =>
        {
            entry.ToTable("Entries");
            entry.HasKey(e => e.Id);
            entry.Property(e => e.Text).HasMaxLength(Contracts.JournalLimits.MaxTextLength).IsRequired();
            // One entry per day; the streak and the lists read by day.
            entry.HasIndex(e => e.Day).IsUnique();
            if (Database.IsNpgsql())
            {
                entry.Property(e => e.SearchVector)
                    .HasColumnType("tsvector")
                    .HasComputedColumnSql(SearchVectorSql, stored: true);
                entry.HasIndex(e => e.SearchVector).HasMethod("GIN");
            }
            else
            {
                entry.Ignore(e => e.SearchVector);
            }
        });

        // Durable events (T38, T78): Timeline (T16) and Search (T17) use them.
        modelBuilder.AddEventOutbox();
    }
}

/// <summary>For `dotnet ef` only: the migrations tools make the context with this factory.</summary>
public class JournalDbContextDesignTimeFactory : IDesignTimeDbContextFactory<JournalDbContext>
{
    public JournalDbContext CreateDbContext(string[] args) =>
        new(HestiaDatabase.DesignTimeOptions<JournalDbContext>(JournalDbContext.SchemaName));
}
