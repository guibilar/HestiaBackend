namespace Hestia.Modules.Calendar.Contracts;

/// <summary>
/// One thing on the Calendar (T103): an event of the Calendar module, or a dated thing of another module (a task due, a
/// birthday). A thing of a whole day has <see cref="AllDay"/>; else <see cref="StartsAt"/> and <see cref="EndsAt"/>
/// say when. The clients show <see cref="Title"/> and <see cref="Text"/> as they are.
/// </summary>
/// <param name="Id">Stable for the same thing and day, and unique on the Calendar: the lower-case module name, a dot, the
/// kind, a dot, and the id; a repeated event adds the day (or the local start) of the occurrence.</param>
/// <param name="Module">The module, for example <c>Calendar</c> or <c>Tasks</c>.</param>
/// <param name="Kind">What it is inside the module, for example <c>event</c>, <c>task-due</c>, or <c>birthday</c>.
/// The clients pick an icon by it.</param>
/// <param name="Title">One line, for example the title of the event.</param>
/// <param name="Text">A second line (for example the place, or "Turns 40"), or null.</param>
/// <param name="AllDay">True for a thing of whole days.</param>
/// <param name="StartDate">The first local day.</param>
/// <param name="EndDate">The last local day (included). The same as <see cref="StartDate"/> for one day.</param>
/// <param name="StartsAt">The start, for a thing with a time; null for a whole day.</param>
/// <param name="EndsAt">The end, for a thing with a time; null for a whole day.</param>
/// <param name="Link">The web route of the thing (for example <c>/tasks/&lt;id&gt;</c>); the Android app maps it.</param>
/// <param name="EventId">For an event of the Calendar module: its id, to open, change, or delete it. Else null.</param>
/// <param name="Recurring">True for an occurrence of a repeated event.</param>
/// <param name="Done">For a task: false while it is open. Null for other things.</param>
public sealed record CalendarItem(
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
    Guid? EventId = null,
    bool Recurring = false,
    bool? Done = null)
{
    /// <summary>A thing of one whole day.</summary>
    public static CalendarItem Day(string id, string module, string kind, string title, string? text, DateOnly day, string? link,
        bool? done = null) =>
        new(id, module, kind, title, text, AllDay: true, day, day, null, null, link, Done: done);
}

/// <summary>
/// The answer of <c>GET /api/calendar</c>: the items of all modules that touch a day from <see cref="From"/> to
/// <see cref="To"/>, by start: on each day the things of the whole day first, then by time.
/// </summary>
/// <param name="Today">Today, in the host's time zone.</param>
/// <param name="TimeZone">That time zone (IANA), for the clients to show the times as the host does.</param>
/// <param name="Modules">Each module that has a source, with the status of its items.</param>
public sealed record CalendarDto(
    DateOnly Today,
    DateOnly From,
    DateOnly To,
    string TimeZone,
    IReadOnlyList<CalendarItem> Items,
    IReadOnlyList<CalendarModuleDto> Modules);

/// <summary>The status of a module's items in one answer.</summary>
public sealed record CalendarModuleDto(string Module, CalendarModuleStatus Status);

public enum CalendarModuleStatus
{
    /// <summary>All items of the module are there.</summary>
    Complete,

    /// <summary>Some items are missing (a part did not load).</summary>
    Partial,

    /// <summary>The module gave no items: its source failed or did not answer in time.</summary>
    Failed
}

/// <summary>Where an event came from.</summary>
public enum CalendarEventSource
{
    /// <summary>The user added it in Hestia.</summary>
    Manual,

    /// <summary>From an <c>.ics</c> file (<c>POST /api/calendar/import</c>). A new import of the same UID updates it.</summary>
    Import
}

/// <summary>An event of the Calendar module.</summary>
/// <param name="Id">The event.</param>
/// <param name="Title">The title.</param>
/// <param name="AllDay">True for an event of whole days: then <see cref="StartDate"/> and <see cref="EndDate"/> are set.</param>
/// <param name="StartDate">The first day of an all-day event (of its first occurrence, for a repeated one).</param>
/// <param name="EndDate">The last day (included) of an all-day event.</param>
/// <param name="StartsAt">The start of an event with a time (of its first occurrence, for a repeated one).</param>
/// <param name="EndsAt">The end of an event with a time.</param>
/// <param name="TimeZone">The IANA time zone of the event. A repeated event repeats at the same local time in it.</param>
/// <param name="Location">The place, or null.</param>
/// <param name="Notes">Free text, or null.</param>
/// <param name="Repeat">The repeat, as an iCalendar RRULE (for example <c>FREQ=WEEKLY;BYDAY=TU,TH</c>), or null.</param>
/// <param name="SkippedDates">The occurrences that do not happen (local days), oldest first.</param>
/// <param name="Source">Added in Hestia, or imported.</param>
/// <param name="Uid">The iCalendar UID, for an imported event.</param>
public sealed record CalendarEventDto(
    Guid Id,
    string Title,
    bool AllDay,
    DateOnly? StartDate,
    DateOnly? EndDate,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    string TimeZone,
    string? Location,
    string? Notes,
    string? Repeat,
    IReadOnlyList<DateOnly> SkippedDates,
    CalendarEventSource Source,
    string? Uid,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// A new event (<c>POST /api/calendar/events</c>), or the new details of one (<c>PUT</c>; all values replace the old
/// ones). An all-day event needs <see cref="StartDate"/>; an event with a time needs <see cref="StartsAt"/>.
/// </summary>
/// <param name="Title">The title (required, at most 200 characters).</param>
/// <param name="AllDay">True for whole days.</param>
/// <param name="StartDate">All day: the first day.</param>
/// <param name="EndDate">All day: the last day (included). Default: the first day.</param>
/// <param name="StartsAt">With a time: the start.</param>
/// <param name="EndsAt">With a time: the end, after the start. Default: one hour after the start.</param>
/// <param name="TimeZone">An IANA time zone. Default: the host's (<c>Hestia:TimeZone</c>).</param>
/// <param name="Location">The place.</param>
/// <param name="Notes">Free text.</param>
/// <param name="Repeat">An iCalendar RRULE, for example <c>FREQ=YEARLY</c> or <c>FREQ=WEEKLY;INTERVAL=2;UNTIL=20271231</c>.</param>
public sealed record CalendarEventRequest(
    string? Title,
    bool AllDay = false,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null,
    string? TimeZone = null,
    string? Location = null,
    string? Notes = null,
    string? Repeat = null);

/// <summary>One occurrence of a repeated event to skip (<c>POST /api/calendar/events/{id}/skip</c>).</summary>
/// <param name="Date">The local day of the occurrence (its <c>startDate</c> on the Calendar).</param>
public sealed record CalendarSkipRequest(DateOnly? Date);

/// <summary>An <c>.ics</c> file to import (<c>POST /api/calendar/import</c>).</summary>
/// <param name="Ics">The text of the file (iCalendar, at most 5 MB).</param>
/// <param name="DryRun">True: only say what would change.</param>
public sealed record CalendarImportRequest(string? Ics, bool DryRun = false);

/// <summary>What an import does with one event of the file.</summary>
public enum CalendarImportAction
{
    /// <summary>A new event.</summary>
    Add,

    /// <summary>An event with the same UID is there, and it changes.</summary>
    Update,

    /// <summary>An event with the same UID is there, with the same details.</summary>
    Same,

    /// <summary>The file cancels the event (<c>STATUS:CANCELLED</c> or <c>METHOD:CANCEL</c>): it is deleted.</summary>
    Delete,

    /// <summary>The event cannot be imported (<see cref="CalendarImportItemDto.Reason"/>).</summary>
    Skip
}

/// <summary>One event of an imported file.</summary>
/// <param name="EventId">The event in Hestia (after the import, or the one that the import changes); null for a new
/// event in a dry run, or a skipped one.</param>
public sealed record CalendarImportItemDto(
    CalendarImportAction Action,
    string Title,
    bool AllDay,
    DateOnly? StartDate,
    DateTimeOffset? StartsAt,
    string? Repeat,
    Guid? EventId,
    string? Reason);

/// <summary>The result of an import: the counts, and each event of the file in file order.</summary>
public sealed record CalendarImportResultDto(
    bool DryRun,
    int Added,
    int Updated,
    int Unchanged,
    int Deleted,
    int Skipped,
    IReadOnlyList<CalendarImportItemDto> Events);

/// <summary>The limits of the Calendar module, for the clients.</summary>
public static class CalendarLimits
{
    public const int MaxTitleLength = 200;
    public const int MaxLocationLength = 300;
    public const int MaxNotesLength = 4000;
    public const int MaxRepeatLength = 500;

    /// <summary>The most days of one <c>GET /api/calendar</c>.</summary>
    public const int MaxRangeDays = 400;

    /// <summary>The biggest <c>.ics</c> text (characters).</summary>
    public const int MaxIcsLength = 5_000_000;

    /// <summary>The most events in one import.</summary>
    public const int MaxImportEvents = 5000;
}
