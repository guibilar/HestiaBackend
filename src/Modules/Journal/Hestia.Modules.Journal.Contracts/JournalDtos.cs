namespace Hestia.Modules.Journal.Contracts;

/// <summary>One journal entry: the text of one day.</summary>
/// <param name="Day">The day of the entry. A day has at most one entry.</param>
public sealed record JournalEntryDto(Guid Id, DateOnly Day, string Text, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>An entry in a list: the start of its text.</summary>
/// <param name="Excerpt">The first <c>JournalLimits.ExcerptLength</c> characters of the text, on one line.</param>
/// <param name="Length">The length of the whole text.</param>
public sealed record JournalEntrySummaryDto(Guid Id, DateOnly Day, string Excerpt, int Length, DateTimeOffset UpdatedAt);

/// <summary>A day of the week strip: whether it has an entry.</summary>
public sealed record JournalDayDto(DateOnly Day, bool Written);

/// <summary>An entry from the same day in an earlier year ("On this day").</summary>
public sealed record OnThisDayDto(int YearsAgo, JournalEntrySummaryDto Entry);

/// <summary>
/// The Journal page (<c>GET /api/journal/summary</c>): today's entry, the streak, the week, and "On this day".
/// </summary>
/// <param name="Date">Today, in the module's time zone.</param>
/// <param name="Streak">
/// The days in a row with an entry, up to today. Without an entry today, the row up to yesterday counts: the streak goes
/// on when the user writes today.
/// </param>
/// <param name="Week">The last 7 days, oldest first; the last one is today.</param>
/// <param name="Today">Today's entry, or null.</param>
/// <param name="OnThisDay">The entries of the same day in earlier years, the latest year first.</param>
/// <param name="EntryCount">All entries.</param>
public sealed record JournalSummaryDto(
    DateOnly Date,
    int Streak,
    IReadOnlyList<JournalDayDto> Week,
    JournalEntryDto? Today,
    IReadOnlyList<OnThisDayDto> OnThisDay,
    int EntryCount);

/// <summary>The body of <c>PUT /api/journal/entries/{day}</c>: the whole text of the day.</summary>
public sealed record SaveJournalEntryRequest(string? Text);

/// <summary>The limits of the Journal module, for the clients.</summary>
public static class JournalLimits
{
    public const int MaxTextLength = 20_000;

    public const int ExcerptLength = 200;

    public const int MaxSearchLength = 200;
}
