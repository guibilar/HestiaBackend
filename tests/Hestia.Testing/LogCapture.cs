using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Hestia.Testing;

/// <summary>One log line that <see cref="LogCapture"/> kept.</summary>
public sealed record CapturedLog(string Category, LogLevel Level, string Message);

/// <summary>
/// A logger provider that keeps every log line of a test host. Add it with
/// <c>services.AddSingleton&lt;ILoggerProvider&gt;(capture)</c>.
/// </summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _lines = new();

    public IReadOnlyList<CapturedLog> Lines => _lines.ToList();

    /// <summary>The lines at this level or above, whose message has the text.</summary>
    public IReadOnlyList<CapturedLog> Find(string text, LogLevel minimum = LogLevel.Trace) =>
        _lines.Where(line => line.Level >= minimum && line.Message.Contains(text, StringComparison.Ordinal)).ToList();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<CapturedLog> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => lines.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception)));
    }
}
