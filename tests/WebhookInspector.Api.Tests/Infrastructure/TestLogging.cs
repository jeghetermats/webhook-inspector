using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace WebhookInspector.Api.Tests.Infrastructure;

public sealed record LogEntry(string Category, LogLevel Level, string Message, string State, string? Exception);

public sealed class TestLogSink
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

    public void Add(LogEntry entry) => _entries.Enqueue(entry);
}

/// <summary>
/// Records the formatted message, every structured state value and the full exception
/// text, so a test can check that nothing sensitive reaches any logging provider.
/// </summary>
public sealed class TestLoggerProvider(TestLogSink sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TestLogger(categoryName, sink);

    public void Dispose()
    {
    }

    private sealed class TestLogger(string category, TestLogSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var stateText = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join("; ", pairs.Select(p => $"{p.Key}={p.Value}"))
                : state?.ToString() ?? string.Empty;

            sink.Add(new LogEntry(category, logLevel, formatter(state, exception), stateText, exception?.ToString()));
        }
    }
}
