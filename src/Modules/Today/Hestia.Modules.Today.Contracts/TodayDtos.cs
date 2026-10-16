namespace Hestia.Modules.Today.Contracts;

/// <summary>
/// The answer of <c>GET /api/today</c>: all cards of the Today screen, from all modules, in display order.
/// The finance cards come first (D11).
/// </summary>
/// <param name="Date">The day that the cards are for, in the time zone of the host (<c>Today:TimeZone</c>).</param>
/// <param name="GeneratedAt">When the host made this answer. The host keeps it for a short time (<c>Today:CacheDuration</c>).</param>
/// <param name="Modules">Each module that gives cards, with its status. A failed module gives no cards.</param>
/// <param name="Cards">The cards: first the tiles, then the "Needs attention" items, each group in module order.</param>
/// <param name="Agenda">"Your day" (T106): the Calendar items of today and of the next days. Null when the Calendar did
/// not answer.</param>
/// <param name="Memories">"On this day" (T106): what happened on this day in earlier years, newest first.</param>
public sealed record TodayDto(
    DateOnly Date,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<TodayModuleDto> Modules,
    IReadOnlyList<TodayCard> Cards,
    TodayAgendaDto? Agenda = null,
    IReadOnlyList<TodayMemoryDto>? Memories = null);

/// <summary>
/// "Your day" on Today (T106): the things of today (events, tasks due, birthdays, …, from the Calendar module, T103), and
/// the next ones of the coming week.
/// </summary>
/// <param name="Today">The items that touch today: the whole-day ones first, then by time.</param>
/// <param name="Upcoming">The items that start in the next 7 days, soonest first (at most 8).</param>
/// <param name="Complete">False when a module's items did not load: the lists can miss some.</param>
public sealed record TodayAgendaDto(IReadOnlyList<TodayAgendaItem> Today, IReadOnlyList<TodayAgendaItem> Upcoming, bool Complete);

/// <summary>
/// One thing of "Your day": the same fields as a Calendar item (<c>GET /api/calendar</c>). A thing of whole days has
/// <see cref="AllDay"/>; else <see cref="StartsAt"/> says when.
/// </summary>
/// <param name="Id">The Calendar item id.</param>
/// <param name="Module">The module, for example <c>Calendar</c> or <c>Tasks</c>.</param>
/// <param name="Kind">What it is: <c>event</c>, <c>task-due</c>, <c>birthday</c>, <c>reach-out</c>, <c>loan-due</c>,
/// <c>document-expires</c>, <c>warranty-ends</c>.</param>
/// <param name="Title">One line.</param>
/// <param name="Text">A second line (the place, "Turns 40"), or null.</param>
/// <param name="AllDay">True for a thing of whole days.</param>
/// <param name="StartDate">The first day.</param>
/// <param name="EndDate">The last day (included).</param>
/// <param name="StartsAt">The start, for a thing with a time.</param>
/// <param name="EndsAt">The end, for a thing with a time.</param>
/// <param name="Link">The web route of the thing; the Android app maps it.</param>
/// <param name="EventId">For an event of the Calendar module: its id.</param>
/// <param name="Done">For a task: false while it is open.</param>
public sealed record TodayAgendaItem(
    string Id,
    string Module,
    string Kind,
    string Title,
    string? Text,
    bool AllDay,
    DateOnly StartDate,
    DateOnly EndDate,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    string? Link,
    Guid? EventId,
    bool? Done);

/// <summary>Something from this day in an earlier year ("On this day", T106), for example a journal entry.</summary>
/// <param name="Module">The module, for example <c>Journal</c>.</param>
/// <param name="Kind">What it is, for example <c>journal-entry</c>.</param>
/// <param name="YearsAgo">How many years ago (1 or more).</param>
/// <param name="Day">The day.</param>
/// <param name="Text">The start of its text (at most 200 characters).</param>
/// <param name="Link">The web route of the thing.</param>
public sealed record TodayMemoryDto(string Module, string Kind, int YearsAgo, DateOnly Day, string Text, string? Link);

/// <summary>One module on Today, and whether all its cards are there.</summary>
public sealed record TodayModuleDto(string Module, TodayModuleStatus Status);

public enum TodayModuleStatus
{
    /// <summary>All cards of the module are there.</summary>
    Complete,

    /// <summary>Some data did not load. The cards that need it have no value and the text "Could not load this".</summary>
    Partial,

    /// <summary>The module gave no cards (an error or a timeout). The clients can show "Could not load".</summary>
    Failed
}

/// <summary>
/// One card on Today. The clients format all amounts and dates themselves (privacy mode, locale): a text has
/// placeholders such as <c>{amount}</c>, and <see cref="Values"/> has a value for each placeholder.
/// </summary>
/// <param name="Id">Stable for the same thing, for example <c>money.budget-over.&lt;budget id&gt;</c>. Use it as a list key.</param>
/// <param name="Module">The module name, for example <c>Money</c>.</param>
/// <param name="Type">A headline tile, or an item of the "Needs attention" list.</param>
/// <param name="Kind">What the card shows, inside its module, for example <c>spent</c> or <c>budget-over</c>. Clients pick the icon with it.</param>
/// <param name="Title">The tile label, for example "Spent this month". For an attention item, a short name of its kind.</param>
/// <param name="Value">The big value of a tile. Null when it is not known ("—").</param>
/// <param name="Text">The tile hint or the attention sentence, with placeholders. Null when there is nothing to say.</param>
/// <param name="Values">The value of each placeholder in <see cref="Text"/>.</param>
/// <param name="Severity">How the card looks: neutral, good, warning, or bad.</param>
/// <param name="Trend">An arrow next to the text, or null for none.</param>
/// <param name="Link">A client route that opens the matching page, for example <c>/money/budgets</c> or <c>/money/rules#unsorted</c>. Null for none.</param>
public sealed record TodayCard(
    string Id,
    string Module,
    TodayCardType Type,
    string Kind,
    string Title,
    TodayValue? Value,
    string? Text,
    IReadOnlyDictionary<string, TodayValue> Values,
    TodaySeverity Severity,
    TodayTrend? Trend,
    string? Link);

public enum TodayCardType
{
    /// <summary>A headline number, for example the four finance tiles.</summary>
    Tile,

    /// <summary>An item of the "Needs attention" list.</summary>
    Attention
}

public enum TodaySeverity
{
    Neutral,
    Good,
    Warning,
    Bad
}

public enum TodayTrend
{
    Up,
    Down
}

/// <summary>
/// One value on a card. Exactly one of <see cref="Number"/>, <see cref="Text"/>, and <see cref="Date"/> is set,
/// as <see cref="Type"/> says. Use the factory methods.
/// </summary>
public sealed record TodayValue(TodayValueType Type, decimal? Number = null, string? Text = null, DateOnly? Date = null)
{
    /// <summary>An amount of money. Clients format it (and hide it in privacy mode).</summary>
    public static TodayValue Money(decimal amount) => new(TodayValueType.Money, Number: amount);

    /// <summary>A whole number, for example a count of transactions or days.</summary>
    public static TodayValue Count(int count) => new(TodayValueType.Count, Number: count);

    /// <summary>A percentage, already rounded, for example 120 for "120%".</summary>
    public static TodayValue Percent(decimal percent) => new(TodayValueType.Percent, Number: percent);

    /// <summary>A day. Clients format it, for example "Jun 30, 2026".</summary>
    public static TodayValue Day(DateOnly date) => new(TodayValueType.Date, Date: date);

    /// <summary>Plain text, shown as it is, for example a name.</summary>
    public static TodayValue Plain(string text) => new(TodayValueType.Text, Text: text);
}

public enum TodayValueType
{
    Money,
    Count,
    Percent,
    Date,
    Text
}
