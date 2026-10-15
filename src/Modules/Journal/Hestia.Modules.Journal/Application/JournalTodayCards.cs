using Hestia.Modules.Today.Contracts;

namespace Hestia.Modules.Journal.Application;

/// <summary>
/// The Journal tile on Today (T21): the streak (days in a row with an entry), and "Not written yet" until today has an
/// entry. The day is the day of the Journal module (<c>Journal:TimeZone</c>), so the tile and the Journal page agree.
/// </summary>
public sealed class JournalTodayCards(JournalService journal) : ITodayCardProvider
{
    public const string ModuleName = "Journal";
    public const string TileId = "journal.streak";

    public string Module => ModuleName;

    /// <summary>After People (130).</summary>
    public int Order => 140;

    public async Task<TodayModuleCards> GetCardsAsync(TodayContext context, CancellationToken cancellationToken)
    {
        var summary = await journal.SummaryAsync(cancellationToken);
        return new TodayModuleCards([Tile(summary.Streak, summary.Today is not null)]);
    }

    public static TodayCard Tile(int streak, bool writtenToday) => new(
        TileId,
        ModuleName,
        TodayCardType.Tile,
        "streak",
        "Journal streak",
        TodayValue.Count(streak),
        writtenToday ? null : "Not written today",
        new Dictionary<string, TodayValue>(),
        writtenToday && streak > 0 ? TodaySeverity.Good : TodaySeverity.Neutral,
        null,
        "/journal");
}
