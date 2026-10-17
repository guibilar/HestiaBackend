using Hestia.Modules.Calendar.Contracts;
using Hestia.Modules.Calendar.Domain;
using Hestia.Modules.Calendar.Infrastructure;
using Hestia.Platform.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.Calendar.Application;

/// <summary>The events of the Calendar (T103): add, change, skip one occurrence, delete, and import an <c>.ics</c> file.</summary>
public sealed class CalendarService(CalendarDbContext db, CalendarDays days)
{
    public async Task<CalendarEventDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Dto(await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            ?? throw new NotFoundException("Calendar event", id));

    public async Task<CalendarEventDto> CreateAsync(CalendarEventRequest request, CancellationToken cancellationToken)
    {
        var now = days.Now;
        var calendarEvent = new CalendarEvent { Id = Guid.CreateVersion7(), Source = CalendarEventSource.Manual, CreatedAt = now };
        Apply(calendarEvent, request);
        calendarEvent.UpdatedAt = now;
        db.Events.Add(calendarEvent);
        await db.SaveChangesAsync(cancellationToken);
        return Dto(calendarEvent);
    }

    /// <summary>Changes all details of the event (all occurrences of a repeated one). The skipped days stay.</summary>
    public async Task<CalendarEventDto> UpdateAsync(Guid id, CalendarEventRequest request, CancellationToken cancellationToken)
    {
        var calendarEvent = await FindAsync(id, cancellationToken);
        Apply(calendarEvent, request);
        calendarEvent.UpdatedAt = days.Now;
        await db.SaveChangesAsync(cancellationToken);
        return Dto(calendarEvent);
    }

    /// <summary>One occurrence of a repeated event does not happen. The other occurrences stay.</summary>
    public async Task<CalendarEventDto> SkipAsync(Guid id, CalendarSkipRequest request, CancellationToken cancellationToken)
    {
        var calendarEvent = await FindAsync(id, cancellationToken);
        if (calendarEvent.Repeat is null)
        {
            throw new InvalidInputException("date", "Only an occurrence of a repeated event can be skipped. Delete the event instead.");
        }
        if (request.Date is not { } date)
        {
            throw new InvalidInputException("date", "Choose the day of the occurrence to skip.");
        }
        calendarEvent.SkippedDates = SkippedDates.Write(SkippedDates.Read(calendarEvent.SkippedDates).Append(date));
        calendarEvent.UpdatedAt = days.Now;
        await db.SaveChangesAsync(cancellationToken);
        return Dto(calendarEvent);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        db.Events.Remove(await FindAsync(id, cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Imports the events of an <c>.ics</c> file. An event with a UID that Hestia has already (from an earlier import)
    /// is updated, so the same invitation twice gives one event. A cancelled event is deleted. A changed occurrence of a
    /// repeated event (RECURRENCE-ID) is its own event, and the repeated event skips that day.
    /// </summary>
    public async Task<CalendarImportResultDto> ImportAsync(CalendarImportRequest request, CancellationToken cancellationToken)
    {
        var read = IcsReader.Read(request.Ics, days.TimeZone);
        var uids = read.Select(e => e.Uid).Distinct().ToList();
        // Batches, so a big file does not make one huge IN list.
        var existing = new List<CalendarEvent>();
        foreach (var chunk in uids.Chunk(500))
        {
            existing.AddRange(await db.Events.Where(e => e.Uid != null && chunk.Contains(e.Uid)).ToListAsync(cancellationToken));
        }
        var byKey = existing.ToDictionary(e => Key(e.Uid!, e.RecurrenceId));
        var lastIndex = read.Select((e, index) => (Key(e.Uid, e.RecurrenceId), index))
            .GroupBy(pair => pair.Item1).ToDictionary(group => group.Key, group => group.Max(pair => pair.index));

        var now = days.Now;
        var items = new List<CalendarImportItemDto>();
        // The masters first, so a changed occurrence finds the repeated event of its file.
        var order = read.Select((e, index) => (Event: e, Index: index)).OrderBy(pair => pair.Event.RecurrenceId is null ? 0 : 1).ThenBy(pair => pair.Index);
        var results = new CalendarImportItemDto[read.Count];
        foreach (var (item, index) in order)
        {
            results[index] = ImportOne(item, index, lastIndex, byKey, now, request.DryRun);
        }
        items.AddRange(results);

        if (!request.DryRun)
        {
            await db.SaveChangesAsync(cancellationToken);
            // The new events have their ids now.
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Action == CalendarImportAction.Add && byKey.TryGetValue(Key(read[i].Uid, read[i].RecurrenceId), out var added))
                {
                    items[i] = items[i] with { EventId = added.Id };
                }
            }
        }
        return new CalendarImportResultDto(
            request.DryRun,
            items.Count(i => i.Action == CalendarImportAction.Add),
            items.Count(i => i.Action == CalendarImportAction.Update),
            items.Count(i => i.Action == CalendarImportAction.Same),
            items.Count(i => i.Action == CalendarImportAction.Delete),
            items.Count(i => i.Action == CalendarImportAction.Skip),
            items);
    }

    private CalendarImportItemDto ImportOne(IcsEvent item, int index, Dictionary<string, int> lastIndex,
        Dictionary<string, CalendarEvent> byKey, DateTimeOffset now, bool dryRun)
    {
        var key = Key(item.Uid, item.RecurrenceId);
        CalendarImportItemDto Result(CalendarImportAction action, Guid? id, string? reason = null) =>
            new(action, item.Title, item.AllDay, item.StartDate, item.StartsAt, item.Repeat, id, reason);

        if (lastIndex[key] != index)
        {
            return Result(CalendarImportAction.Skip, null, "The file has this event again later; the later one counts.");
        }
        byKey.TryGetValue(key, out var current);
        if (item.Cancelled)
        {
            // A cancelled occurrence (RECURRENCE-ID): the repeated event skips its day, with or without a changed copy here.
            var skips = SkipInRepeat(item, byKey, now, dryRun, out var repeated);
            if (current is null)
            {
                return repeated is not null
                    ? Result(skips ? CalendarImportAction.Update : CalendarImportAction.Same, repeated.Id)
                    : Result(CalendarImportAction.Skip, null, "The file cancels this event, and Hestia does not have it.");
            }
            if (!dryRun)
            {
                db.Events.Remove(current);
                byKey.Remove(key);
            }
            return Result(CalendarImportAction.Delete, current.Id);
        }
        if (item.Problem is not null)
        {
            return Result(CalendarImportAction.Skip, current?.Id, item.Problem);
        }

        // A changed occurrence: the repeated event of the same UID skips its day.
        SkipInRepeat(item, byKey, now, dryRun, out _);

        var target = current ?? new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Source = CalendarEventSource.Import,
            Uid = item.Uid,
            RecurrenceId = item.RecurrenceId,
            CreatedAt = now
        };
        var before = current is null ? null : Snapshot(current);
        var skippedNow = current is null ? item.SkippedDates : SkippedDates.Read(current.SkippedDates).Union(item.SkippedDates);
        var after = new EventValues(item.Title, item.AllDay, item.StartDate, item.EndDate, item.StartsAt, item.EndsAt, item.TimeZone,
            item.Location, item.Notes, item.Repeat, SkippedDates.Write(skippedNow));
        if (before == after)
        {
            return Result(CalendarImportAction.Same, current!.Id);
        }
        if (!dryRun)
        {
            Set(target, after);
            target.UpdatedAt = now;
            if (current is null)
            {
                db.Events.Add(target);
                byKey[key] = target;
            }
        }
        return Result(current is null ? CalendarImportAction.Add : CalendarImportAction.Update, current?.Id);
    }

    /// <summary>
    /// An occurrence (RECURRENCE-ID) of a repeated event that Hestia has: the repeated event skips its day. True when the
    /// day was not skipped yet.
    /// </summary>
    private static bool SkipInRepeat(IcsEvent item, Dictionary<string, CalendarEvent> byKey, DateTimeOffset now, bool dryRun,
        out CalendarEvent? repeated)
    {
        repeated = null;
        if (item.RecurrenceDay is not { } day || !byKey.TryGetValue(Key(item.Uid, null), out var master) || master.Repeat is null)
        {
            return false;
        }
        repeated = master;
        var skipped = SkippedDates.Write(SkippedDates.Read(master.SkippedDates).Append(day));
        if (skipped == master.SkippedDates)
        {
            return false;
        }
        if (!dryRun)
        {
            master.SkippedDates = skipped;
            master.UpdatedAt = now;
        }
        return true;
    }

    private static string Key(string uid, string? recurrenceId) => $"{uid}\n{recurrenceId}";

    /// <summary>The values that an import compares, to tell "the same" from "changed".</summary>
    private sealed record EventValues(string Title, bool AllDay, DateOnly? StartDate, DateOnly? EndDate, DateTimeOffset? StartsAt,
        DateTimeOffset? EndsAt, string TimeZone, string? Location, string? Notes, string? Repeat, string SkippedDates);

    private static EventValues Snapshot(CalendarEvent e) =>
        new(e.Title, e.AllDay, e.StartDate, e.EndDate, e.StartsAt, e.EndsAt, e.TimeZone, e.Location, e.Notes, e.Repeat, e.SkippedDates);

    private static void Set(CalendarEvent e, EventValues values)
    {
        e.Title = values.Title;
        e.AllDay = values.AllDay;
        e.StartDate = values.StartDate;
        e.EndDate = values.EndDate;
        e.StartsAt = values.StartsAt;
        e.EndsAt = values.EndsAt;
        e.TimeZone = values.TimeZone;
        e.Location = values.Location;
        e.Notes = values.Notes;
        e.Repeat = values.Repeat;
        e.SkippedDates = values.SkippedDates;
    }

    private void Apply(CalendarEvent calendarEvent, CalendarEventRequest request)
    {
        var title = request.Title?.Trim() ?? string.Empty;
        if (title.Length == 0)
        {
            throw new InvalidInputException("title", "Give the event a title.");
        }
        if (title.Length > CalendarLimits.MaxTitleLength)
        {
            throw new InvalidInputException("title", $"The title can have at most {CalendarLimits.MaxTitleLength} characters.");
        }
        var zoneId = string.IsNullOrWhiteSpace(request.TimeZone) ? days.TimeZone.Id : request.TimeZone.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone) || !zone.HasIanaId)
        {
            throw new InvalidInputException("timeZone", $"\"{zoneId}\" is not an IANA time zone (for example America/Edmonton).");
        }

        calendarEvent.Title = title;
        calendarEvent.AllDay = request.AllDay;
        calendarEvent.TimeZone = zone.Id;
        calendarEvent.Location = Optional(request.Location, CalendarLimits.MaxLocationLength, "location");
        calendarEvent.Notes = Optional(request.Notes, CalendarLimits.MaxNotesLength, "notes");
        calendarEvent.Repeat = Occurrences.CheckRepeat(request.Repeat);
        if (request.AllDay)
        {
            if (request.StartDate is not { } start)
            {
                throw new InvalidInputException("startDate", "An all-day event needs its first day.");
            }
            var end = request.EndDate ?? start;
            if (end < start)
            {
                throw new InvalidInputException("endDate", "The last day cannot be before the first day.");
            }
            calendarEvent.StartDate = start;
            calendarEvent.EndDate = end;
            calendarEvent.StartsAt = null;
            calendarEvent.EndsAt = null;
        }
        else
        {
            if (request.StartsAt is not { } startsAt)
            {
                throw new InvalidInputException("startsAt", "An event with a time needs its start.");
            }
            var endsAt = request.EndsAt ?? startsAt.AddHours(1);
            if (endsAt < startsAt)
            {
                throw new InvalidInputException("endsAt", "The end cannot be before the start.");
            }
            calendarEvent.StartsAt = startsAt.ToUniversalTime();
            calendarEvent.EndsAt = endsAt.ToUniversalTime();
            calendarEvent.StartDate = null;
            calendarEvent.EndDate = null;
        }
    }

    private static string? Optional(string? text, int max, string field)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        if (value.Length > max)
        {
            throw new InvalidInputException(field, $"{field} can have at most {max} characters.");
        }
        return value;
    }

    private async Task<CalendarEvent> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Events.FirstOrDefaultAsync(e => e.Id == id, cancellationToken) ?? throw new NotFoundException("Calendar event", id);

    public static CalendarEventDto Dto(CalendarEvent e) => new(e.Id, e.Title, e.AllDay, e.StartDate, e.EndDate, e.StartsAt, e.EndsAt,
        e.TimeZone, e.Location, e.Notes, e.Repeat, SkippedDates.Read(e.SkippedDates), e.Source, e.Uid, e.CreatedAt, e.UpdatedAt);
}
