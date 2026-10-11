using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Hestia.Testing;

/// <summary>
/// Records the events that reach the host's consumers. <see cref="RecordEvents"/> adds one consumer per event type,
/// like a module consumer, so a test sees what a real consumer receives.
/// </summary>
public sealed class EventRecorder
{
    private readonly ConcurrentQueue<object> _events = new();
    private readonly SemaphoreSlim _signal = new(0);

    public IReadOnlyList<object> Events => _events.ToList();

    public void Record(object @event)
    {
        _events.Enqueue(@event);
        _signal.Release();
    }

    /// <summary>Waits for the first event of type <typeparamref name="T"/> that matches. Fails after the timeout.</summary>
    public async Task<T> WaitForAsync<T>(Func<T, bool>? match = null, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            if (_events.OfType<T>().FirstOrDefault(e => match?.Invoke(e) ?? true) is { } found)
            {
                return found;
            }
            var left = deadline - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero || !await _signal.WaitAsync(left))
            {
                if (_events.OfType<T>().FirstOrDefault(e => match?.Invoke(e) ?? true) is { } late)
                {
                    return late;
                }
                throw new TimeoutException(
                    $"No {typeof(T).Name} reached the consumers. Recorded: {string.Join(", ", Events.Select(e => e.GetType().Name))}.");
            }
        }
    }

    /// <summary>Waits a short time, then returns the events of type <typeparamref name="T"/> (to show that none came).</summary>
    public async Task<IReadOnlyList<T>> AfterQuietPeriodAsync<T>(TimeSpan? wait = null)
    {
        await Task.Delay(wait ?? TimeSpan.FromSeconds(1));
        return _events.OfType<T>().ToList();
    }
}

/// <summary>A consumer for one event type that hands each event to the <see cref="EventRecorder"/>.</summary>
public sealed class RecordingConsumer<TEvent>(EventRecorder recorder) where TEvent : class
{
    public void Consume(TEvent message) => recorder.Record(message);
}

/// <summary>Adds a <see cref="RecordingConsumer{TEvent}"/> for each event type to the host's Wolverine setup.</summary>
public sealed class RecordEvents(IReadOnlyList<Type> eventTypes) : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        foreach (var eventType in eventTypes)
        {
            options.Discovery.IncludeType(typeof(RecordingConsumer<>).MakeGenericType(eventType));
        }
    }
}

public static class EventRecorderServiceCollectionExtensions
{
    /// <summary>Records the events of these types in the host (for <see cref="HestiaApiFactory"/>'s configureServices).</summary>
    public static IServiceCollection AddEventRecorder(this IServiceCollection services, EventRecorder recorder,
        params Type[] eventTypes)
    {
        services.AddSingleton(recorder);
        services.AddSingleton<IWolverineExtension>(new RecordEvents(eventTypes));
        return services;
    }
}
