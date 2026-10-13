using Hestia.Modules.Documents.Domain;
using Hestia.Platform.Events;
using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hestia.Modules.Documents.Infrastructure;

public class DocumentsDbContext(DbContextOptions<DocumentsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "documents";

    /// <summary>
    /// The full-text index of a document (PostgreSQL): the title and the tags weigh most (A), then the notes (B), then the
    /// OCR text (C). Each text is in two configurations: "english" (stems, so "warranties" finds "warranty") and "simple"
    /// (every word as it is, for names, numbers, and other languages).
    /// </summary>
    public const string SearchVectorSql =
        "setweight(to_tsvector('english'::regconfig, \"Title\"), 'A') || " +
        "setweight(to_tsvector('simple'::regconfig, \"Title\" || ' ' || \"Tags\"), 'A') || " +
        "setweight(to_tsvector('english'::regconfig, coalesce(\"Notes\", '')), 'B') || " +
        "setweight(to_tsvector('simple'::regconfig, coalesce(\"Notes\", '')), 'B') || " +
        "setweight(to_tsvector('english'::regconfig, coalesce(\"OcrText\", '')), 'C') || " +
        "setweight(to_tsvector('simple'::regconfig, coalesce(\"OcrText\", '')), 'C')";

    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentFile> Files => Set<DocumentFile>();
    public DbSet<DocumentLink> Links => Set<DocumentLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Document>(document =>
        {
            document.ToTable("Documents");
            document.HasKey(d => d.Id);
            document.Property(d => d.Title).HasMaxLength(Document.MaxTitleLength).IsRequired();
            document.Property(d => d.Kind).HasConversion<string>().HasMaxLength(20);
            document.Property(d => d.Tags).HasMaxLength(DocumentTags.MaxStoredLength).IsRequired();
            document.Property(d => d.Notes).HasMaxLength(Document.MaxNotesLength);
            document.Property(d => d.OcrStatus).HasConversion<string>().HasMaxLength(20);
            document.Property(d => d.OcrError).HasMaxLength(Document.MaxOcrErrorLength);
            document.HasIndex(d => d.ExpiresOn);

            document.HasMany(d => d.Files).WithOne().HasForeignKey(f => f.DocumentId).OnDelete(DeleteBehavior.Cascade);
            document.HasMany(d => d.Links).WithOne().HasForeignKey(l => l.DocumentId).OnDelete(DeleteBehavior.Cascade);

            // The full-text index exists only in PostgreSQL. The in-memory test database searches the text itself
            // (see DocumentService).
            if (Database.IsNpgsql())
            {
                document.Property(d => d.SearchVector)
                    .HasColumnType("tsvector")
                    .HasComputedColumnSql(SearchVectorSql, stored: true);
                document.HasIndex(d => d.SearchVector).HasMethod("GIN");
            }
            else
            {
                document.Ignore(d => d.SearchVector);
            }
        });

        modelBuilder.Entity<DocumentFile>(file =>
        {
            file.ToTable("DocumentFiles");
            file.HasKey(f => f.Id);
            file.Property(f => f.FileName).HasMaxLength(DocumentFile.MaxFileNameLength).IsRequired();
            file.Property(f => f.ContentType).HasMaxLength(DocumentFile.MaxContentTypeLength).IsRequired();
            file.Property(f => f.Sha256).HasMaxLength(DocumentFile.Sha256Length).IsRequired();
            file.Property(f => f.StorageKey).HasMaxLength(DocumentFile.StorageKeyLength).IsRequired();
            // A deleted document removes its file from the volume only when no other document has the same content.
            file.HasIndex(f => f.StorageKey);
        });

        modelBuilder.Entity<DocumentLink>(link =>
        {
            link.ToTable("DocumentLinks");
            link.HasKey(l => l.Id);
            link.Property(l => l.Kind).HasMaxLength(DocumentLink.MaxKindLength).IsRequired();
            link.Property(l => l.TargetId).HasMaxLength(DocumentLink.MaxTargetIdLength).IsRequired();
            // One link per document and thing.
            link.HasIndex(l => new { l.DocumentId, l.Kind, l.TargetId }).IsUnique();
            // GET /api/documents/by-link: the documents of one thing.
            link.HasIndex(l => new { l.Kind, l.TargetId });
        });

        // The inbox for future consumers of this module (T36): the table "EventInbox" in this schema.
        modelBuilder.AddEventInbox();
        // Durable events (T38): other modules (Timeline, Search) depend on them. See DocumentsModule.
        modelBuilder.AddEventOutbox();
    }
}

/// <summary>For `dotnet ef` only: the migrations tools make the context with this factory.</summary>
public class DocumentsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<DocumentsDbContext>
{
    public DocumentsDbContext CreateDbContext(string[] args) =>
        new(HestiaDatabase.DesignTimeOptions<DocumentsDbContext>(DocumentsDbContext.SchemaName));
}
