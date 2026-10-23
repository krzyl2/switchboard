using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Switchboard.Tests.Support;

/// <summary>Records every rendered log line (message plus exception text) so tests can assert what never appears.</summary>
internal sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<(LogLevel Level, string Text)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Text)> Entries => [.. _entries];

    public string AllText => string.Join('\n', _entries.Select(e => e.Text));

    public ILogger<T> For<T>() => new Logger<T>(new LoggerFactory([this]));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(LogCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (exception is not null)
            {
                text += " | " + exception;
            }

            owner._entries.Enqueue((logLevel, text));
        }
    }
}
