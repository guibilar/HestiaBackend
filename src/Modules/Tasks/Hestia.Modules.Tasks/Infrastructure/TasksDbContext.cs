using Hestia.Modules.Tasks.Domain;
using Hestia.Platform.Events;
using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hestia.Modules.Tasks.Infrastructure;

public class TasksDbContext(DbContextOptions<TasksDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "tasks";

    /// <summary>The length of an enum column (the enum name as text).</summary>
    private const int EnumLength = 20;

    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<TaskCompletion> Completions => Set<TaskCompletion>();
    public DbSet<TaskLink> Links => Set<TaskLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TaskItem>(task =>
        {
            task.ToTable("Tasks");
            task.HasKey(t => t.Id);
            task.Property(t => t.Title).HasMaxLength(TaskItem.MaxTitleLength).IsRequired();
            task.Property(t => t.Notes).HasMaxLength(TaskItem.MaxNotesLength);
            task.Property(t => t.Tags).HasMaxLength(TaskTags.MaxStoredLength).IsRequired();
            task.Property(t => t.Status).HasConversion<string>().HasMaxLength(EnumLength);
            task.Property(t => t.RepeatUnit).HasConversion<string>().HasMaxLength(EnumLength);
            task.Property(t => t.RepeatMode).HasConversion<string>().HasMaxLength(EnumLength);
            task.Ignore(t => t.Repeat);
            // The lists by status and due date: today, overdue, upcoming (the summary, the alerts, the Today tile).
            task.HasIndex(t => new { t.Status, t.DueOn });

            task.HasMany(t => t.Completions).WithOne().HasForeignKey(c => c.TaskId).OnDelete(DeleteBehavior.Cascade);
            task.HasMany(t => t.Links).WithOne().HasForeignKey(l => l.TaskId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskCompletion>(completion =>
        {
            completion.ToTable("TaskCompletions");
            completion.HasKey(c => c.Id);
            // The last completions of one task (GET /api/tasks/{id}, undo).
            completion.HasIndex(c => new { c.TaskId, c.DoneAt });
        });

        modelBuilder.Entity<TaskLink>(link =>
        {
            link.ToTable("TaskLinks");
            link.HasKey(l => l.Id);
            link.Property(l => l.Kind).HasMaxLength(TaskLink.MaxKindLength).IsRequired();
            link.Property(l => l.TargetId).HasMaxLength(TaskLink.MaxTargetIdLength).IsRequired();
            // One link per task and thing.
            link.HasIndex(l => new { l.TaskId, l.Kind, l.TargetId }).IsUnique();
            // GET /api/tasks/by-link, and the DocumentDeleted consumer: the tasks of one thing.
            link.HasIndex(l => new { l.Kind, l.TargetId });
        });

        // The inbox of the consumers of this module (T36): the DocumentDeleted consumer.
        modelBuilder.AddEventInbox();
        // Durable events (T38): Timeline (T16) depends on them. See TasksModule.
        modelBuilder.AddEventOutbox();
    }
}

/// <summary>For `dotnet ef` only: the migrations tools make the context with this factory.</summary>
public class TasksDbContextDesignTimeFactory : IDesignTimeDbContextFactory<TasksDbContext>
{
    public TasksDbContext CreateDbContext(string[] args) =>
        new(HestiaDatabase.DesignTimeOptions<TasksDbContext>(TasksDbContext.SchemaName));
}
