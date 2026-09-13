using Microsoft.Extensions.Logging;

namespace EDButtkicker.Services;

/// <summary>One logged failure, as a support bundle reports it. The message is not sanitized yet.</summary>
public sealed record RecordedError(
    DateTime TimestampUtc,
    string Level,
    string Category,
    string Message,
    string? ExceptionType);

/// <summary>
/// A bounded, thread-safe ring of the errors this session has logged, kept so a support bundle can
/// answer "what went wrong?" without asking the reporter to find, read and hand over a log file -
/// which is how raw paths and journal lines end up in public issues.
///
/// Only <see cref="LogLevel.Error"/> and above are kept: a bundle is evidence for a report, not a
/// trace.
/// </summary>
public sealed class RecentErrorLog
{
    /// <summary>Errors retained. Old enough to show what led up to a failure, small enough to read.</summary>
    public const int MaxErrors = 25;

    private readonly object _lock = new();
    private readonly List<RecordedError> _errors = new();
    private readonly TimeProvider _timeProvider;

    public RecentErrorLog(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public void Record(LogLevel level, string category, string message, Exception? exception)
    {
        if (level < LogLevel.Error)
        {
            return;
        }

        var entry = new RecordedError(
            _timeProvider.GetUtcNow().UtcDateTime,
            level.ToString(),
            category,
            message,
            exception?.GetType().FullName);

        lock (_lock)
        {
            _errors.Add(entry);

            if (_errors.Count > MaxErrors)
            {
                _errors.RemoveRange(0, _errors.Count - MaxErrors);
            }
        }
    }

    /// <summary>Most recent first, capped at <paramref name="limit"/>.</summary>
    public IReadOnlyList<RecordedError> GetRecent(int limit)
    {
        if (limit <= 0)
        {
            return Array.Empty<RecordedError>();
        }

        lock (_lock)
        {
            return _errors
                .AsEnumerable()
                .Reverse()
                .Take(limit)
                .ToList();
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _errors.Count;
            }
        }
    }
}

/// <summary>
/// Feeds <see cref="RecentErrorLog"/> from the logging pipeline, so the bundle reports the same
/// failures the console showed rather than a second, hand-maintained list of things that can go
/// wrong. Registered alongside the console provider in Program; nothing here ever logs.
/// </summary>
[ProviderAlias("RecentErrors")]
public sealed class RecentErrorLogProvider : ILoggerProvider
{
    private readonly RecentErrorLog _log;

    public RecentErrorLogProvider(RecentErrorLog log)
    {
        _log = log;
    }

    public ILogger CreateLogger(string categoryName) => new RecentErrorLogger(_log, categoryName);

    public void Dispose()
    {
        // Nothing is held open: the ring lives in the service container, not in this provider.
    }

    private sealed class RecentErrorLogger : ILogger
    {
        private readonly RecentErrorLog _log;
        private readonly string _category;

        public RecentErrorLogger(RecentErrorLog log, string category)
        {
            _log = log;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // The formatter is what the console provider uses too, so the bundle shows the message
            // the user saw - with the structured placeholders already filled in.
            _log.Record(logLevel, _category, formatter(state, exception), exception);
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
