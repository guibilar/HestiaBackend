using Hestia.Modules.Journal.Contracts;
using Hestia.Modules.Journal.Domain;
using Hestia.Modules.Journal.Infrastructure;
using Hestia.Platform.Exceptions;
using Hestia.Platform.Paging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hestia.Modules.Journal.Application;

/// <summary>The filters of <c>GET /api/journal/entries</c>.</summary>
/// <param name="Text">Words in the text (full-text search); null for all.</param>
public sealed record JournalQuery(string? Text, DateOnly? From, DateOnly? To, int Page = 1, int PageSize = 20);

/// <summary>The journal: one entry per day, the streak, and "On this day" (T21).</summary>
public sealed class JournalService(JournalDbContext db, JournalDays days)
{
    public const int MaxPageSize = 100;

    /// <summary>How far back "On this day" looks.</summary>
    public const int OnThisDayYears = 30;

    public async Task<JournalSummaryDto> SummaryAsync(CancellationToken cancellationToken)
    {
        var today = days.Today;
        var count = await db.Entries.CountAsync(cancellationToken);

        // The streak needs the days in a row before today: read the days of the last year, enough for most streaks, and
        // more only when the row reaches that far.
        var written = new HashSet<DateOnly>();
        var from = today.AddDays(-366);
        while (true)
        {
            var batch = await db.Entries.AsNoTracking().Where(e => e.Day >= from && e.Day <= today)
                .Select(e => e.Day).ToListAsync(cancellationToken);
            written.UnionWith(batch);
            var streak = Streak(written, today);
            var start = (written.Contains(today) ? today : today.AddDays(-1)).AddDays(-streak + 1);
            if (streak == 0 || start > from || batch.Count == 0)
            {
                break;
            }
            from = from.AddDays(-366);
        }

        var todayEntry = await db.Entries.AsNoTracking().FirstOrDefaultAsync(e => e.Day == today, cancellationToken);
        var onThisDay = await OnThisDayAsync(today, cancellationToken);
        var week = Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6)).Select(d => new JournalDayDto(d, written.Contains(d))).ToList();
        return new JournalSummaryDto(today, Streak(written, today), week, todayEntry is null ? null : Dto(todayEntry), onThisDay, count);
    }

    /// <summary>
    /// The days in a row with an entry, ending today, or ending yesterday when today has no entry yet.
    /// </summary>
    public static int Streak(IReadOnlySet<DateOnly> written, DateOnly today)
    {
        var day = written.Contains(today) ? today : today.AddDays(-1);
        var streak = 0;
        while (written.Contains(day))
        {
            streak++;
            day = day.AddDays(-1);
        }
        return streak;
    }

    /// <summary>
    /// The days that are "this day" in earlier years: the same month and day. On Feb 28 of a year that is not a leap
    /// year, Feb 29 of the earlier years counts too.
    /// </summary>
    public static IReadOnlyList<DateOnly> SameDays(DateOnly today, int years)
    {
        var result = new List<DateOnly>();
        for (var back = 1; back <= years; back++)
        {
            var year = today.Year - back;
            if (year < 1)
            {
                break;
            }
            if (today.Month == 2 && today.Day == 29 && !DateTime.IsLeapYear(year))
            {
                // Feb 29 shows Feb 28 of other years only through their own Feb 28; nothing here.
                continue;
            }
            result.Add(new DateOnly(year, today.Month, today.Day));
            if (today.Month == 2 && today.Day == 28 && !DateTime.IsLeapYear(today.Year) && DateTime.IsLeapYear(year))
            {
                result.Add(new DateOnly(year, 2, 29));
            }
        }
        return result;
    }

    private async Task<IReadOnlyList<OnThisDayDto>> OnThisDayAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var same = SameDays(today, OnThisDayYears);
        var entries = await db.Entries.AsNoTracking().Where(e => same.Contains(e.Day)).OrderByDescending(e => e.Day)
            .ToListAsync(cancellationToken);
        return entries.Select(e => new OnThisDayDto(today.Year - e.Day.Year, Summary(e))).ToList();
    }

    public async Task<PaginatedList<JournalEntrySummaryDto>> ListAsync(JournalQuery query, CancellationToken cancellationToken)
    {
        if (query.Page < 1 || query.PageSize is < 1 or > MaxPageSize)
        {
            throw new InvalidInputException("pageSize", $"page must be 1 or more, and pageSize from 1 to {MaxPageSize}.");
        }
        if (query.From is { } start && query.To is { } end && end < start)
        {
            throw new InvalidInputException("to", "to must be on or after from.");
        }
        var text = query.Text?.Trim() ?? string.Empty;
        if (text.Length > JournalLimits.MaxSearchLength)
        {
            throw new InvalidInputException("q", $"The search text can have at most {JournalLimits.MaxSearchLength} characters.");
        }

        var entries = db.Entries.AsNoTracking();
        if (query.From is { } from)
        {
            entries = entries.Where(e => e.Day >= from);
        }
        if (query.To is { } to)
        {
            entries = entries.Where(e => e.Day <= to);
        }

        IOrderedQueryable<JournalEntry> ordered;
        if (text.Length == 0)
        {
            ordered = entries.OrderByDescending(e => e.Day);
        }
        else if (db.Database.IsNpgsql())
        {
            // The full-text index: web search syntax ("two words", "exact phrase", -not), in the "english" and the
            // "simple" configurations, best match first.
            entries = entries.Where(e => e.SearchVector!.Matches(
                EF.Functions.WebSearchToTsQuery("english", text).Or(EF.Functions.WebSearchToTsQuery("simple", text))));
            ordered = entries
                .OrderByDescending(e => e.SearchVector!.Rank(
                    EF.Functions.WebSearchToTsQuery("english", text).Or(EF.Functions.WebSearchToTsQuery("simple", text))))
                .ThenByDescending(e => e.Day);
        }
        else
        {
            // The in-memory test database has no full-text index: each word must be in the text.
            foreach (var word in text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                entries = entries.Where(e => e.Text.ToLower().Contains(word));
            }
            ordered = entries.OrderByDescending(e => e.Day);
        }

        var total = await entries.CountAsync(cancellationToken);
        var items = await ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(cancellationToken);
        return new PaginatedList<JournalEntrySummaryDto>
        {
            Items = items.Select(Summary).ToList(),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = total
        };
    }

    public async Task<JournalEntryDto> GetAsync(DateOnly day, CancellationToken cancellationToken) =>
        await db.Entries.AsNoTracking().FirstOrDefaultAsync(e => e.Day == day, cancellationToken) is { } entry
            ? Dto(entry)
            : throw new NotFoundException($"Journal entry of {day:yyyy-MM-dd}");

    /// <summary>Writes the text of a day: a new entry, or a new text for the entry of that day.</summary>
    public async Task<(JournalEntryDto Entry, bool Created)> SaveAsync(DateOnly day, SaveJournalEntryRequest request,
        CancellationToken cancellationToken)
    {
        var text = request.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            throw new InvalidInputException("text", "Write something, or delete the entry.");
        }
        if (text.Length > JournalLimits.MaxTextLength)
        {
            throw new InvalidInputException("text", $"An entry can have at most {JournalLimits.MaxTextLength} characters.");
        }
        if (day > days.Today)
        {
            throw new InvalidInputException("day", "An entry cannot be for a day after today.");
        }

        var now = days.Now;
        var entry = await db.Entries.FirstOrDefaultAsync(e => e.Day == day, cancellationToken);
        var created = entry is null;
        if (entry is null)
        {
            entry = new JournalEntry { Day = day, Text = text, CreatedAt = now, UpdatedAt = now };
            db.Entries.Add(entry);
        }
        else if (entry.Text == text)
        {
            // Nothing changed: no new time, no event.
            return (Dto(entry), false);
        }
        else
        {
            entry.Text = text;
            entry.UpdatedAt = now;
        }
        db.RaiseEvent(new JournalEntrySaved(entry.Id, day, created, now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (created && exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        })
        {
            // A second save of a new day at the same time (two devices): the unique index refused this one.
            throw new ConflictException("The entry of this day was just written somewhere else. Load it again.");
        }
        return (Dto(entry), created);
    }

    public async Task DeleteAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var entry = await db.Entries.FirstOrDefaultAsync(e => e.Day == day, cancellationToken)
            ?? throw new NotFoundException($"Journal entry of {day:yyyy-MM-dd}");
        db.Entries.Remove(entry);
        db.RaiseEvent(new JournalEntryDeleted(entry.Id, day, days.Now));
        await db.SaveChangesAsync(cancellationToken);
    }

    public static JournalEntryDto Dto(JournalEntry entry) => new(entry.Id, entry.Day, entry.Text, entry.CreatedAt, entry.UpdatedAt);

    public static JournalEntrySummaryDto Summary(JournalEntry entry) =>
        new(entry.Id, entry.Day, Excerpt(entry.Text), entry.Text.Length, entry.UpdatedAt);

    /// <summary>The start of a text on one line, at most <see cref="JournalLimits.ExcerptLength"/> characters.</summary>
    public static string Excerpt(string text)
    {
        var line = string.Join(' ', text.Split((char[])['\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= JournalLimits.ExcerptLength ? line : line[..(JournalLimits.ExcerptLength - 1)].TrimEnd() + "…";
    }
}
