using System.Data.Common;
using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Hestia.Platform.Events;

/// <summary>
/// The post-commit dispatcher for module events (D7). It publishes the events of a <see cref="ModuleDbContext"/> after
/// the change that includes them is committed:
/// <list type="bullet">
/// <item>Without an explicit transaction, <c>SaveChanges</c> commits: the events go out after it.</item>
/// <item>In an explicit transaction, the events go out when the transaction commits, and never when it rolls back.</item>
/// <item>A failed save publishes nothing. Its events stay on the context for a later save.</item>
/// </list>
/// The in-process path is not durable: if the process stops between the commit and the publish, the event is lost.
/// A module that cannot lose an event needs an outbox in its schema (a later task).
/// </summary>
public sealed class ModuleEventsInterceptor(IEventPublisher publisher, ILogger<ModuleEventsInterceptor> logger)
    : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        (eventData.Context as ModuleDbContext)?.SaveStarting();
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        (eventData.Context as ModuleDbContext)?.SaveStarting();
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (Saved(eventData.Context) is { } context)
        {
            PublishCommittedAsync(context, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        if (Saved(eventData.Context) is { } context)
        {
            await PublishCommittedAsync(context, cancellationToken);
        }
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) =>
        (eventData.Context as ModuleDbContext)?.SaveFailed();

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        (eventData.Context as ModuleDbContext)?.SaveFailed();
        return Task.CompletedTask;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData) =>
        (eventData.Context as ModuleDbContext)?.SaveFailed();

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        (eventData.Context as ModuleDbContext)?.SaveFailed();
        return Task.CompletedTask;
    }

    /// <summary>The context, when the saved change is committed already (no explicit transaction).</summary>
    private static ModuleDbContext? Saved(DbContext? context)
    {
        if (context is not ModuleDbContext module)
        {
            return null;
        }
        module.SaveSucceeded();
        return module.Database.CurrentTransaction is null ? module : null;
    }

    /// <summary>Publishes the committed events. The change is stored already, so a failure here is logged, not thrown.</summary>
    internal async ValueTask PublishCommittedAsync(ModuleDbContext context, CancellationToken cancellationToken)
    {
        foreach (var @event in context.TakeCommitted())
        {
            try
            {
                await publisher.PublishAsync(@event, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The event {EventType} was not published. The change that raised it is committed.",
                    @event.GetType().Name);
            }
        }
    }
}

/// <summary>
/// The transaction half of <see cref="ModuleEventsInterceptor"/>: an explicit transaction that commits publishes the
/// events of its saves; one that rolls back or fails drops them.
/// </summary>
public sealed class ModuleEventsTransactionInterceptor(ModuleEventsInterceptor saves) : DbTransactionInterceptor
{
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (eventData.Context is ModuleDbContext context)
        {
            saves.PublishCommittedAsync(context, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
    }

    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is ModuleDbContext context)
        {
            await saves.PublishCommittedAsync(context, cancellationToken);
        }
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        (eventData.Context as ModuleDbContext)?.DropUncommitted();

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        (eventData.Context as ModuleDbContext)?.DropUncommitted();
        return Task.CompletedTask;
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        (eventData.Context as ModuleDbContext)?.DropUncommitted();

    public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        (eventData.Context as ModuleDbContext)?.DropUncommitted();
        return Task.CompletedTask;
    }
}
