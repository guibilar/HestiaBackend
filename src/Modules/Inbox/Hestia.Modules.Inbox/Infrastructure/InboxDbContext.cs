using Hestia.Modules.Inbox.Contracts;
using Hestia.Modules.Inbox.Domain;
using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hestia.Modules.Inbox.Infrastructure;

public class InboxDbContext(DbContextOptions<InboxDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "inbox";

    public DbSet<InboxItem> Items => Set<InboxItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<InboxItem>(item =>
        {
            item.ToTable("Items");
            item.HasKey(i => i.Id);
            item.Property(i => i.Text).HasMaxLength(InboxLimits.MaxTextLength).IsRequired();
            item.Property(i => i.SortedTo).HasMaxLength(InboxLimits.MaxSortedToLength);
            // The open notes, newest first.
            item.HasIndex(i => new { i.DoneAt, i.CreatedAt });
            // T102: email notes.
            item.Property(i => i.Source).HasConversion<string>().HasMaxLength(20);
            item.Property(i => i.From).HasMaxLength(InboxLimits.MaxFromLength);
            item.Property(i => i.MessageId).HasMaxLength(InboxLimits.MaxMessageIdLength);
            item.Property(i => i.DocumentIds).IsRequired();
            // The same email never makes two notes.
            item.HasIndex(i => i.MessageId).IsUnique();
        });
    }
}

/// <summary>For `dotnet ef` only: the migrations tools make the context with this factory.</summary>
public class InboxDbContextDesignTimeFactory : IDesignTimeDbContextFactory<InboxDbContext>
{
    public InboxDbContext CreateDbContext(string[] args) =>
        new(HestiaDatabase.DesignTimeOptions<InboxDbContext>(InboxDbContext.SchemaName));
}
