using System.Globalization;
using System.Text.RegularExpressions;
using Hestia.Modules.Documents.Contracts;
using Hestia.Platform.Exceptions;

namespace Hestia.Modules.Documents.Domain;

/// <summary>
/// The tags of a document. They are stored in one text column as <c>|tag1|tag2|</c>, so a filter by tag is a text
/// search for <c>|tag|</c>, and the full-text index can read them (a PostgreSQL array cannot go into a generated column).
/// </summary>
public static partial class DocumentTags
{
    public const int MaxTags = 20;
    public const int MaxTagLength = 50;
    public const char Separator = '|';

    /// <summary>The longest stored value: 20 tags of 50 characters, each after a separator, and the last separator.</summary>
    public const int MaxStoredLength = MaxTags * (MaxTagLength + 1) + 1;

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    /// <summary>
    /// The tags as the user gave them, cleaned: each value can hold several tags with commas between them. A tag is
    /// trimmed, in lower case, with single spaces. Empty tags and repeats are left out.
    /// </summary>
    /// <exception cref="InvalidInputException">A tag is too long or has a "|", or there are too many tags.</exception>
    public static List<string> Normalize(IEnumerable<string?>? values)
    {
        var tags = new List<string>();
        foreach (var value in values ?? [])
        {
            foreach (var part in (value ?? string.Empty).Split(','))
            {
                var tag = Spaces().Replace(part.Trim(), " ").ToLowerInvariant();
                if (tag.Length == 0 || tags.Contains(tag))
                {
                    continue;
                }
                if (tag.Contains(Separator))
                {
                    throw new InvalidInputException("tags", $"A tag cannot have the character \"{Separator}\".");
                }
                if (tag.Length > MaxTagLength)
                {
                    throw new InvalidInputException("tags", $"A tag can have at most {MaxTagLength} characters.");
                }
                tags.Add(tag);
            }
        }
        if (tags.Count > MaxTags)
        {
            throw new InvalidInputException("tags", $"A document can have at most {MaxTags} tags.");
        }
        return tags;
    }

    public static string Store(IReadOnlyCollection<string> tags) =>
        tags.Count == 0 ? string.Empty : $"{Separator}{string.Join(Separator, tags)}{Separator}";

    public static List<string> Read(string stored) =>
        stored.Split(Separator, StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>The text to search for in the stored value, to find one tag.</summary>
    public static string Pattern(string tag) => $"{Separator}{tag}{Separator}";
}

/// <summary>The details of a document that the user sets: checked and cleaned for an upload and for a change.</summary>
public sealed record DocumentDetails(
    string Title,
    DocumentKind Kind,
    List<string> Tags,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    string? Notes)
{
    /// <exception cref="InvalidInputException">A value cannot be used.</exception>
    public static DocumentDetails Create(string? title, DocumentKind? kind, IEnumerable<string?>? tags, DateOnly? issuedOn,
        DateOnly? expiresOn, string? notes)
    {
        var cleanTitle = title?.Trim() ?? string.Empty;
        if (cleanTitle.Length == 0)
        {
            throw new InvalidInputException("title", "Enter a title.");
        }
        if (cleanTitle.Length > Document.MaxTitleLength)
        {
            throw new InvalidInputException("title", $"The title can have at most {Document.MaxTitleLength} characters.");
        }
        if (kind is not { } documentKind || !Enum.IsDefined(documentKind))
        {
            throw new InvalidInputException("kind", $"Choose the kind of document: {string.Join(", ", Enum.GetNames<DocumentKind>())}.");
        }
        var cleanNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (cleanNotes?.Length > Document.MaxNotesLength)
        {
            throw new InvalidInputException("notes", $"The notes can have at most {Document.MaxNotesLength} characters.");
        }
        if (issuedOn is { } issued && expiresOn is { } expires && expires < issued)
        {
            throw new InvalidInputException("expiresOn", "The expiry date must be on or after the issue date.");
        }
        return new DocumentDetails(cleanTitle, documentKind, DocumentTags.Normalize(tags), issuedOn, expiresOn, cleanNotes);
    }

    /// <summary>A kind from text (an upload form or a query string), in any letter case. Numbers are not kinds.</summary>
    /// <exception cref="InvalidInputException">The text is not a kind.</exception>
    public static DocumentKind? ParseKind(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var text = value.Trim();
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
            Enum.TryParse<DocumentKind>(text, ignoreCase: true, out var kind) && Enum.IsDefined(kind))
        {
            return kind;
        }
        throw new InvalidInputException(field, $"\"{text}\" is not a kind of document. Use one of: {string.Join(", ", Enum.GetNames<DocumentKind>())}.");
    }

    /// <summary>A day from text, as <c>yyyy-MM-dd</c>.</summary>
    /// <exception cref="InvalidInputException">The text is not a day in this format.</exception>
    public static DateOnly? ParseDay(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return day;
        }
        throw new InvalidInputException(field, $"\"{value.Trim()}\" is not a day. Use the format yyyy-MM-dd, for example 2026-10-13.");
    }

    /// <summary>Applies the details to a document.</summary>
    public void ApplyTo(Document document)
    {
        document.Title = Title;
        document.Kind = Kind;
        document.Tags = DocumentTags.Store(Tags);
        document.IssuedOn = IssuedOn;
        document.ExpiresOn = ExpiresOn;
        document.Notes = Notes;
    }
}
