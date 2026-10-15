using Hestia.Modules.Today.Contracts;

namespace Hestia.Modules.Inbox.Application;

/// <summary>The Inbox tile on Today (T18): the count of notes to sort. Warning from 10 open notes.</summary>
public sealed class InboxTodayCards(InboxService inbox) : ITodayCardProvider
{
    public const string ModuleName = "Inbox";
    public const string TileId = "inbox.open";

    /// <summary>From this many open notes, the tile asks for a look.</summary>
    public const int WarnFrom = 10;

    public string Module => ModuleName;

    /// <summary>After Journal (140).</summary>
    public int Order => 150;

    public async Task<TodayModuleCards> GetCardsAsync(TodayContext context, CancellationToken cancellationToken)
    {
        var open = await inbox.OpenCountAsync(cancellationToken);
        return new TodayModuleCards([Tile(open)]);
    }

    public static TodayCard Tile(int open) => new(
        TileId,
        ModuleName,
        TodayCardType.Tile,
        "open",
        "Inbox",
        TodayValue.Count(open),
        open == 0 ? "Nothing to sort" : "to sort",
        new Dictionary<string, TodayValue>(),
        open >= WarnFrom ? TodaySeverity.Warning : TodaySeverity.Neutral,
        null,
        "/inbox");
}
