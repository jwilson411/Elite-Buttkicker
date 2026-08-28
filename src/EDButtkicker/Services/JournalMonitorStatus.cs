namespace EDButtkicker.Services;

/// <summary>What the journal watcher is doing, as opposed to what the configuration says.</summary>
public enum JournalWatchState
{
    /// <summary>The monitor has not run yet (the process just started, or it is not hosted).</summary>
    NotStarted,

    /// <summary>The configured folder cannot be watched yet; the monitor is re-checking it.</summary>
    Waiting,

    /// <summary>A watcher is attached to the folder.</summary>
    Watching,

    /// <summary>Monitoring stopped because of an error.</summary>
    Faulted,

    /// <summary>Monitoring stopped on shutdown.</summary>
    Stopped
}

/// <summary>An immutable read of the watcher state, safe to serialise while the monitor runs.</summary>
public sealed record JournalMonitorSnapshot(
    JournalWatchState State,
    string? Path,
    string Reason,
    string? ActiveFile,
    DateTime? SinceUtc,
    DateTime? LastLineUtc);

/// <summary>
/// The journal watcher's real state, published by <see cref="JournalMonitorService"/> and read by
/// the health API. The dashboard used to call journal monitoring "online" whenever the folder
/// existed; this reports whether a watcher is actually attached, and why not when it is not.
/// It also carries the re-check signal the health retry raises, so "Retry" re-attaches immediately
/// instead of waiting for the monitor's next sweep.
/// </summary>
public sealed class JournalMonitorStatus
{
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _recheck = new(0, 1);
    private readonly object _lock = new();

    private JournalMonitorSnapshot _snapshot = new(
        JournalWatchState.NotStarted,
        Path: null,
        Reason: "Journal monitoring has not started yet.",
        ActiveFile: null,
        SinceUtc: null,
        LastLineUtc: null);

    public JournalMonitorStatus(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public JournalMonitorSnapshot Current
    {
        get
        {
            lock (_lock)
            {
                return _snapshot;
            }
        }
    }

    public void ReportWaiting(string? path, string reason)
    {
        lock (_lock)
        {
            var unchanged = _snapshot.State == JournalWatchState.Waiting && _snapshot.Path == path;

            _snapshot = _snapshot with
            {
                State = JournalWatchState.Waiting,
                Path = path,
                Reason = reason,
                ActiveFile = null,
                SinceUtc = unchanged ? _snapshot.SinceUtc : UtcNow
            };
        }
    }

    public void ReportWatching(string path, string? activeFile)
    {
        lock (_lock)
        {
            var unchanged = _snapshot.State == JournalWatchState.Watching
                && _snapshot.Path == path
                && _snapshot.ActiveFile == activeFile;

            _snapshot = _snapshot with
            {
                State = JournalWatchState.Watching,
                Path = path,
                Reason = activeFile == null
                    ? "Watching the journal folder; Elite Dangerous has not written a journal file yet."
                    : $"Reading {activeFile}.",
                ActiveFile = activeFile,
                SinceUtc = unchanged ? _snapshot.SinceUtc : UtcNow
            };
        }
    }

    public void ReportLinesRead()
    {
        lock (_lock)
        {
            _snapshot = _snapshot with { LastLineUtc = UtcNow };
        }
    }

    public void ReportFaulted(string reason)
    {
        lock (_lock)
        {
            _snapshot = _snapshot with
            {
                State = JournalWatchState.Faulted,
                Reason = reason,
                ActiveFile = null,
                SinceUtc = UtcNow
            };
        }
    }

    public void ReportStopped(string reason)
    {
        lock (_lock)
        {
            _snapshot = _snapshot with
            {
                State = JournalWatchState.Stopped,
                Reason = reason,
                ActiveFile = null,
                SinceUtc = UtcNow
            };
        }
    }

    /// <summary>Asks the monitor to look at the configured folder again, right now.</summary>
    public void RequestRecheck()
    {
        lock (_lock)
        {
            if (_recheck.CurrentCount == 0)
            {
                _recheck.Release();
            }
        }
    }

    /// <summary>
    /// Waits for a re-check request, or for <paramref name="timeout"/> to pass. Returns true when a
    /// request arrived, so the monitor can tell an on-demand retry from its periodic sweep.
    /// </summary>
    public Task<bool> WaitForRecheckAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _recheck.WaitAsync(timeout, cancellationToken);

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;
}
