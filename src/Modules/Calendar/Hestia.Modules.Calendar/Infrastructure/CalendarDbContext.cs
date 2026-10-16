using Hestia.Modules.Calendar.Contracts;
using Hestia.Modules.Calendar.Domain;
using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hestia.Modules.Calendar.Infrastructure;

public class CalendarDbContext(DbContextOptions<CalendarDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "calendar";

    public DbSet<CalendarEvent> Events => Set<CalendarEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<CalendarEvent>(calendarEvent =>
        {
            calendarEvent.ToTable("Events");
            calendarEvent.HasKey(e => e.Id);
            calendarEvent.Property(e => e.Title).HasMaxLength(CalendarLimits.MaxTitleLength).IsRequired();
            calendarEvent.Property(e => e.TimeZone).HasMaxLength(CalendarEvent.MaxTimeZoneLength).IsRequired();
            calendarEvent.Property(e => e.Location).HasMaxLength(CalendarLimits.MaxLocationLength);
            calendarEvent.Property(e => e.Notes).HasMaxLength(CalendarLimits.MaxNotesLength);
            calendarEvent.Property(e => e.Repeat).HasMaxLength(CalendarLimits.MaxRepeatLength);
            calendarEvent.Property(e => e.SkippedDates).IsRequired();
            calendarEvent.Property(e => e.Source).HasConversion<string>().HasMaxLength(20);
            calendarEvent.Property(e => e.Uid).HasMaxLength(CalendarEvent.MaxUidLength);
            calendarEvent.Property(e => e.RecurrenceId).HasMaxLength(CalendarEvent.MaxRecurrenceIdLength);
            // The view of a range reads by start.
            calendarEvent.HasIndex(e => e.StartDate);
            calendarEvent.HasIndex(e => e.StartsAt);
            // An import finds the event of a UID.
            calendarEvent.HasIndex(e => new { e.Uid, e.RecurrenceId });
        });
    }
}

/// <summary>For `dotnet ef` only: the migrations tools make the context with this factory.</summary>
public class CalendarDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CalendarDbContext>
{
    public CalendarDbContext CreateDbContext(string[] args) =>
        new(HestiaDatabase.DesignTimeOptions<CalendarDbContext>(CalendarDbContext.SchemaName));
}
