using NpgsqlTypes;

namespace Hestia.Modules.Journal.Domain;

/// <summary>The journal text of one day. A day has at most one entry (a unique index).</summary>
public class JournalEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateOnly Day { get; set; }

    public string Text { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The full-text index of the text (PostgreSQL only; a computed column).</summary>
    public NpgsqlTsVector? SearchVector { get; set; }
}
