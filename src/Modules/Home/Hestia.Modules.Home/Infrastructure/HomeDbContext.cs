using Hestia.Modules.Home.Domain;
using Hestia.Platform.Events;
using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hestia.Modules.Home.Infrastructure;

public class HomeDbContext(DbContextOptions<HomeDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "home";

    /// <summary>
    /// The full-text index of an item (PostgreSQL): the name, the brand, the model, the serial number, and the tags weigh
    /// most (A), then the category (B), then the notes (C). The name, the category, and the notes are in two
    /// configurations: "english" (stems, so "chairs" finds "chair") and "simple" (every word as it is). The brand, the
    /// model, the serial number, and the tags are names and numbers: "simple" only.
    /// </summary>
    public const string SearchVectorSql =
        "setweight(to_tsvector('english'::regconfig, \"Name\"), 'A') || " +
        "setweight(to_tsvector('simple'::regconfig, \"Name\" || ' ' || coalesce(\"Brand\", '') || ' ' || " +
        "coalesce(\"Model\", '') || ' ' || coalesce(\"SerialNumber\", '') || ' ' || \"Tags\"), 'A') || " +
        "setweight(to_tsvector('english'::regconfig, coalesce(\"Category\", '')), 'B') || " +
        "setweight(to_tsvector('simple'::regconfig, coalesce(\"Category\", '')), 'B') || " +
        "setweight(to_tsvector('english'::regconfig, coalesce(\"Notes\", '')), 'C') || " +
        "setweight(to_tsvector('simple'::regconfig, coalesce(\"Notes\", '')), 'C')";

    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<ItemLink> Links => Set<ItemLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Room>(room =>
        {
            room.ToTable("Rooms");
            room.HasKey(r => r.Id);
            room.Property(r => r.Name).HasMaxLength(Room.MaxNameLength).IsRequired();
            // One room per name. The service checks it first (any letter case); the index is the last guard.
            room.HasIndex(r => r.Name).IsUnique();
        });

        modelBuilder.Entity<Item>(item =>
        {
            item.ToTable("Items");
            item.HasKey(i => i.Id);
            item.Property(i => i.Name).HasMaxLength(Item.MaxNameLength).IsRequired();
            item.Property(i => i.Category).HasMaxLength(Item.MaxCategoryLength);
            item.Property(i => i.Brand).HasMaxLength(Item.MaxBrandLength);
            item.Property(i => i.Model).HasMaxLength(Item.MaxModelLength);
            item.Property(i => i.SerialNumber).HasMaxLength(Item.MaxSerialNumberLength);
            item.Property(i => i.Notes).HasMaxLength(Item.MaxNotesLength);
            item.Property(i => i.Tags).HasMaxLength(ItemTags.MaxStoredLength).IsRequired();
            item.Property(i => i.PurchasePrice).HasPrecision(Item.AmountPrecision, Item.AmountScale);
            item.Property(i => i.CurrentValue).HasPrecision(Item.AmountPrecision, Item.AmountScale);
            item.Ignore(i => i.Value);
            // The warranty filter, the summary, and the warranty alerts.
            item.HasIndex(i => i.WarrantyUntil);

            // A room with items cannot be deleted (the service answers 409; the key is the last guard).
            item.HasOne<Room>().WithMany().HasForeignKey(i => i.RoomId).OnDelete(DeleteBehavior.Restrict);
            item.HasMany(i => i.Links).WithOne().HasForeignKey(l => l.ItemId).OnDelete(DeleteBehavior.Cascade);

            // The full-text index exists only in PostgreSQL. The in-memory test database searches the text itself
            // (see ItemService).
            if (Database.IsNpgsql())
            {
                item.Property(i => i.SearchVector)
                    .HasColumnType("tsvector")
                    .HasComputedColumnSql(SearchVectorSql, stored: true);
                item.HasIndex(i => i.SearchVector).HasMethod("GIN");
            }
            else
            {
                item.Ignore(i => i.SearchVector);
            }
        });

        modelBuilder.Entity<ItemLink>(link =>
        {
            link.ToTable("ItemLinks");
            link.HasKey(l => l.Id);
            link.Property(l => l.Kind).HasMaxLength(ItemLink.MaxKindLength).IsRequired();
            link.Property(l => l.TargetId).HasMaxLength(ItemLink.MaxTargetIdLength).IsRequired();
            // One link per item and thing.
            link.HasIndex(l => new { l.ItemId, l.Kind, l.TargetId }).IsUnique();
            // GET /api/home/items/by-link, and the DocumentDeleted and TaskDeleted consumers: the items of one thing.
            link.HasIndex(l => new { l.Kind, l.TargetId });
        });

        // The inbox of the consumers of this module (T36): DocumentDeleted and TaskDeleted.
        modelBuilder.AddEventInbox();
        // Durable events (T38): Documents and Tasks remove their links on ItemDeleted, and Timeline (T16) uses them.
        modelBuilder.AddEventOutbox();
    }
}

/// <summary>For `dotnet ef` only: the migrations tools make the context with this factory.</summary>
public class HomeDbContextDesignTimeFactory : IDesignTimeDbContextFactory<HomeDbContext>
{
    public HomeDbContext CreateDbContext(string[] args) =>
        new(HestiaDatabase.DesignTimeOptions<HomeDbContext>(HomeDbContext.SchemaName));
}
