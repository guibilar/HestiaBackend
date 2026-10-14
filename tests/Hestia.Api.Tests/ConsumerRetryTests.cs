using System.Collections.Concurrent;
using Hestia.Api.Events;
using Hestia.Modules.Sample.Domain;
using Hestia.Modules.Sample.Infrastructure;
using Hestia.Platform.Events;
using Hestia.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wolverine;

namespace Hestia.Api.Tests;

/// <summary>
/// The retry policy for consumers (T52, <see cref="ConsumerRetries"/>). A short database failure ends in a retry, not in
/// the error queue. Other errors go to the error queue at once, with an error log.
/// </summary>
public class ConsumerRetryTests
{
    private const string MovedToErrorQueue = "was moved to the error queue";

    [Fact]
    public async Task ADatabaseFailure_InALocalQueue_IsTriedAgain()
    {
        var logs = new LogCapture();
        await using var factory = Start(logs);
        var message = new FlakyTestEvent(Guid.CreateVersion7(), FlakyTestEvent.ShortFailure);

        await PublishAsync(factory, message);

        await Eventually(() => Task.FromResult(FlakyTestConsumer.AttemptsOf(message)), attempts => attempts >= 2);
        await Task.Delay(500);
        Assert.Equal(2, FlakyTestConsumer.AttemptsOf(message));
        Assert.Null(logs.Find(MovedToErrorQueue));
    }

    [Fact]
    public async Task ABug_InALocalQueue_IsNotTriedAgain_AndLogsAnError()
    {
        var logs = new LogCapture();
        await using var factory = Start(logs);
        var message = new FlakyTestEvent(Guid.CreateVersion7(), FlakyTestEvent.Bug);

        await PublishAsync(factory, message);

        var line = await Eventually(() => Task.FromResult(logs.Find(MovedToErrorQueue)), found => found is not null);
        Assert.NotNull(line);
        await Task.Delay(500);
        Assert.Equal(1, FlakyTestConsumer.AttemptsOf(message));
    }

    [Fact]
    public async Task ADatabaseFailure_ThatDoesNotGoAway_EndsInTheErrorQueue_AfterAllTries()
    {
        var logs = new LogCapture();
        await using var factory = Start(logs);
        var message = new FlakyTestEvent(Guid.CreateVersion7(), FlakyTestEvent.LongFailure);

        await PublishAsync(factory, message);

        var line = await Eventually(() => Task.FromResult(logs.Find(MovedToErrorQueue)), found => found is not null);
        Assert.NotNull(line);
        Assert.Equal(ConsumerRetries.Delays.Length + 1, FlakyTestConsumer.AttemptsOf(message));
    }

    [Fact]
    public async Task ADatabaseFailure_InAnInvoke_IsTriedAgain()
    {
        // The module outbox sender delivers with InvokeAsync (T38): the retries apply there too.
        await using var factory = Start(new LogCapture());
        var message = new FlakyTestEvent(Guid.CreateVersion7(), FlakyTestEvent.ShortFailure);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(message);
        }

        Assert.Equal(2, FlakyTestConsumer.AttemptsOf(message));
    }

    [Fact]
    public void TransientErrors_AreDatabaseErrorsThatCanGoAway()
    {
        Assert.True(ConsumerRetries.IsTransient(new DbUpdateException("A failed save.")));
        Assert.True(ConsumerRetries.IsTransient(new DbUpdateConcurrencyException("A changed row.")));
        Assert.True(ConsumerRetries.IsTransient(new NpgsqlException("The connection broke.", new IOException("Reset."))));
        Assert.False(ConsumerRetries.IsTransient(new NpgsqlException("A bad query.")));
        Assert.False(ConsumerRetries.IsTransient(new InvalidOperationException("A bug.")));
        Assert.False(ConsumerRetries.IsTransient(new ArgumentException("A bad event.")));
    }

    [Fact]
    public async Task TheSameEvent_TwiceAtTheSameTime_ChangesTheDataOnce_WithoutTheErrorQueue()
    {
        Assert.SkipUnless(TestDatabase.OnPostgres, $"Needs PostgreSQL: set {TestDatabase.Variable}.");
        var logs = new LogCapture();
        await using var factory = Start(logs);
        var message = new RaceTestEvent(Guid.CreateVersion7(), Guid.CreateVersion7());

        // Two deliveries of one event, in parallel (a local queue runs several messages at a time). Both check the inbox
        // before either saves, so one save fails on the inbox key.
        await PublishAsync(factory, message);
        await PublishAsync(factory, message);

        await Eventually(() => Task.FromResult(RaceTestConsumer.AttemptsOf(message)), attempts => attempts >= 3);
        await Task.Delay(500);
        Assert.Equal(1, RaceTestConsumer.CollisionsOf(message));
        // Two first tries, and one retry that finds the inbox row.
        Assert.Equal(3, RaceTestConsumer.AttemptsOf(message));
        var (changes, inbox) = await factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<SampleDbContext>();
            return (await db.Activity.CountAsync(entry => entry.ItemId == message.ItemId),
                await db.Set<InboxEntry>().CountAsync(entry => entry.EventId == message.EventId));
        });
        Assert.Equal((1, 1), (changes, inbox));
        Assert.Null(logs.Find(MovedToErrorQueue));
    }

    private static HestiaApiFactory Start(LogCapture logs)
    {
        var factory = new HestiaApiFactory(configureServices: services =>
        {
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddSingleton<IWolverineExtension>(new AddTestConsumers());
        });
        factory.CreateClient();
        return factory;
    }

    private static async Task PublishAsync(HestiaApiFactory factory, object message)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(message);
    }

    private static async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var value = await read();
            if (done(value) || DateTimeOffset.UtcNow > deadline)
            {
                return value;
            }
            await Task.Delay(50);
        }
    }

    /// <summary>Keeps every log line of the host.</summary>
    private sealed class LogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public string? Find(string text) => _lines.FirstOrDefault(line => line.Contains(text, StringComparison.Ordinal));

        public ILogger CreateLogger(string categoryName) => new Logger(_lines);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => lines.Enqueue(formatter(state, exception));
        }
    }
}

/// <summary>A test event for <see cref="FlakyTestConsumer"/>.</summary>
public sealed record FlakyTestEvent(Guid EventId, string Kind)
{
    /// <summary>The first try fails with a database error, the second works.</summary>
    public const string ShortFailure = "short";

    /// <summary>Every try fails with a database error.</summary>
    public const string LongFailure = "long";

    /// <summary>Every try fails with an error that a retry cannot fix.</summary>
    public const string Bug = "bug";
}

/// <summary>Fails as its event says, and counts its tries.</summary>
public sealed class FlakyTestConsumer
{
    private static readonly ConcurrentDictionary<Guid, int> Attempts = new();

    public static int AttemptsOf(FlakyTestEvent message) => Attempts.GetValueOrDefault(message.EventId);

    [ChangesNoData("A test consumer: it only counts.")]
    public void Consume(FlakyTestEvent message)
    {
        var attempt = Attempts.AddOrUpdate(message.EventId, 1, (_, count) => count + 1);
        switch (message.Kind)
        {
            case FlakyTestEvent.ShortFailure when attempt == 1:
            case FlakyTestEvent.LongFailure:
                throw new DbUpdateException("A database failure (test).");
            case FlakyTestEvent.Bug:
                throw new InvalidOperationException("A bug (test).");
        }
    }
}

/// <summary>A test event for <see cref="RaceTestConsumer"/>.</summary>
public sealed record RaceTestEvent(Guid EventId, Guid ItemId);

/// <summary>
/// An idempotent consumer (the inbox of the Sample module) that waits after its inbox check until two deliveries of the
/// same event reach that point. Then both save, and one save fails on the inbox key.
/// </summary>
public sealed class RaceTestConsumer(SampleDbContext db)
{
    public const string InboxName = "Tests.RaceTestConsumer";

    private static readonly ConcurrentDictionary<Guid, int> Attempts = new();
    private static readonly ConcurrentDictionary<Guid, int> Collisions = new();
    private static readonly ConcurrentDictionary<Guid, Gate> Gates = new();

    public static int AttemptsOf(RaceTestEvent message) => Attempts.GetValueOrDefault(message.EventId);

    public static int CollisionsOf(RaceTestEvent message) => Collisions.GetValueOrDefault(message.EventId);

    public async Task Consume(RaceTestEvent message, CancellationToken cancellationToken)
    {
        Attempts.AddOrUpdate(message.EventId, 1, (_, count) => count + 1);
        if (!await db.TryReceiveAsync(message.EventId, InboxName, cancellationToken))
        {
            return;
        }
        db.Activity.Add(new SampleActivityEntry
        {
            Id = Guid.CreateVersion7(),
            ItemId = message.ItemId,
            Name = "Race",
            ReceivedAt = DateTimeOffset.UtcNow
        });

        await Gates.GetOrAdd(message.EventId, _ => new Gate()).PassAsync();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            Collisions.AddOrUpdate(message.EventId, 1, (_, count) => count + 1);
            throw;
        }
    }

    /// <summary>Opens when two callers reached it. A third caller passes at once.</summary>
    private sealed class Gate
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public Task PassAsync()
        {
            if (Interlocked.Increment(ref _count) >= 2)
            {
                _open.TrySetResult();
            }
            // A failure here (not transient) goes to the error queue, so the test shows that the deliveries were not
            // parallel.
            return _open.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}

/// <summary>Adds the test consumers of <see cref="ConsumerRetryTests"/> to one test host.</summary>
public sealed class AddTestConsumers : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.Discovery.IncludeType(typeof(FlakyTestConsumer));
        options.Discovery.IncludeType(typeof(RaceTestConsumer));
    }
}
