using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EDButtkicker.Services;

/// <summary>What the consumer has to do for one canonical path.</summary>
public enum PatternWatchAction
{
    /// <summary>Re-read one pattern file (optionally after removing the path it was renamed from).</summary>
    Reload,

    /// <summary>Drop one pattern file from the catalog.</summary>
    Remove,

    /// <summary>Re-read the whole directory, used when watcher events were lost.</summary>
    ReloadAll
}

/// <summary>One unit of serialized work handed to the consumer.</summary>
public sealed record PatternWatchWork(PatternWatchAction Action, string FullPath, string? RemovedPath = null);

/// <summary>
/// Timings for <see cref="PatternFileWatchQueue"/>. Tests shrink these; the defaults are what the
/// running app uses.
/// </summary>
public sealed class PatternWatchOptions
{
    /// <summary>How long a path stays quiet before its pending work runs.</summary>
    public TimeSpan DebounceWindow { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Gap between two identical size/mtime probes that marks a write as finished.</summary>
    public TimeSpan StabilityWindow { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Upper bound on stability deferral, so a file written to forever still gets read.</summary>
    public TimeSpan MaxStabilityWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Queue depth. Past this the queue asks for a full reload instead of growing.</summary>
    public int Capacity { get; init; } = 256;
}

/// <summary>
/// Serialized, debounced queue between the pattern <see cref="FileSystemWatcher"/> and the catalog.
///
/// The watcher raises Created/Changed/Renamed/Deleted on thread-pool threads and fires several
/// times for a single save. Producers only ever call <see cref="Enqueue"/> (never blocking, never
/// async void); one consumer loop coalesces events by canonical path, waits until the file's
/// size and last-write time stop moving, and then runs the handler - one item at a time, so a
/// reload can never overlap another reload or a remove for the same catalog.
///
/// The channel is bounded: if it ever fills, the overflow is not silently dropped - the consumer
/// asks for a full <see cref="PatternWatchAction.ReloadAll"/> instead, which is the only truthful
/// recovery once individual events are gone. Every wait honours the shutdown token, so a pending
/// debounce delay ends immediately on dispose.
/// </summary>
public sealed class PatternFileWatchQueue : IDisposable
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Channel<WatchEvent> _events;
    private readonly Dictionary<string, Pending> _pending;
    private readonly Func<PatternWatchWork, CancellationToken, Task> _handler;
    private readonly PatternWatchOptions _options;
    private readonly ILogger _logger;

    private int _overflowed;

    public PatternFileWatchQueue(
        Func<PatternWatchWork, CancellationToken, Task> handler,
        PatternWatchOptions? options = null,
        ILogger? logger = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _options = options ?? new PatternWatchOptions();
        _logger = logger ?? NullLogger.Instance;
        _pending = new Dictionary<string, Pending>(PathComparer);

        // Wait + TryWrite never blocks: a full queue returns false, which is the overflow
        // signal. DropWrite cannot be used here because its TryWrite returns true after
        // discarding the item, so the consumer would never know events were lost.
        _events = Channel.CreateBounded<WatchEvent>(new BoundedChannelOptions(_options.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>Queues one watcher event. Called from watcher threads; never blocks or throws.</summary>
    public void Enqueue(PatternWatchAction action, string fullPath, string? oldFullPath = null)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        if (!_events.Writer.TryWrite(new WatchEvent(action, fullPath, oldFullPath)))
        {
            // Either the queue is full or shut down. A dropped event means the catalog would be
            // stale, so remember it and let the consumer re-read everything.
            if (Interlocked.Exchange(ref _overflowed, 1) == 0)
            {
                _logger.LogWarning(
                    "Pattern watch queue is full ({Capacity} events); falling back to a full reload",
                    _options.Capacity);
            }
        }
    }

    /// <summary>Runs the single consumer loop until <paramref name="cancellationToken"/> fires.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_pending.Count == 0)
                {
                    if (!await _events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                else
                {
                    var delay = NextDueDelay(DateTime.UtcNow);
                    if (delay > TimeSpan.Zero)
                    {
                        await WaitForEventOrTimeoutAsync(delay, cancellationToken).ConfigureAwait(false);
                    }
                }

                Drain();
                await ProcessOverflowAsync(cancellationToken).ConfigureAwait(false);
                await ProcessDueAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            _events.Writer.TryComplete();
            _pending.Clear();
        }
    }

    /// <summary>Stops accepting new events. Pending work ends with the consumer's token.</summary>
    public void Complete() => _events.Writer.TryComplete();

    public void Dispose() => Complete();

    private void Drain()
    {
        while (_events.Reader.TryRead(out var watchEvent))
        {
            Merge(watchEvent, DateTime.UtcNow);
        }
    }

    private void Merge(WatchEvent watchEvent, DateTime nowUtc)
    {
        var key = Canonical(watchEvent.FullPath);

        if (!_pending.TryGetValue(key, out var pending))
        {
            pending = new Pending { DeadlineUtc = nowUtc + _options.MaxStabilityWait };
            _pending[key] = pending;
        }

        // The last event for a path wins: a create followed by a delete is a delete, and a delete
        // followed by a re-create is a reload. Only one of them ever reaches the handler.
        pending.Action = watchEvent.Action;

        if (watchEvent.Action == PatternWatchAction.Remove)
        {
            pending.RemovedPath = null;
        }
        else if (watchEvent.OldFullPath != null)
        {
            // A rename is a remove of the source plus a reload of the target, carried on one item
            // so the two halves can never be interleaved with another path's work.
            pending.RemovedPath = watchEvent.OldFullPath;
            _pending.Remove(Canonical(watchEvent.OldFullPath));
        }

        pending.DueAtUtc = nowUtc + _options.DebounceWindow;
        pending.Probed = false;
    }

    private TimeSpan NextDueDelay(DateTime nowUtc)
    {
        var earliest = DateTime.MaxValue;
        foreach (var pending in _pending.Values)
        {
            if (pending.DueAtUtc < earliest)
            {
                earliest = pending.DueAtUtc;
            }
        }

        return earliest == DateTime.MaxValue ? TimeSpan.Zero : earliest - nowUtc;
    }

    private async Task WaitForEventOrTimeoutAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(delay);

        try
        {
            await _events.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The debounce window elapsed with no new event - that is the signal to run.
        }
    }

    private async Task ProcessOverflowAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _overflowed, 0) == 0)
        {
            return;
        }

        // A full reload supersedes every queued per-file item.
        _pending.Clear();
        await InvokeAsync(new PatternWatchWork(PatternWatchAction.ReloadAll, string.Empty), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ProcessDueAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;

        var due = _pending
            .Where(kv => kv.Value.DueAtUtc <= nowUtc)
            .OrderBy(kv => kv.Value.DueAtUtc)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_pending.TryGetValue(key, out var pending))
            {
                continue;
            }

            if (pending.Action == PatternWatchAction.Reload && !IsWriteStable(key, pending, DateTime.UtcNow))
            {
                continue;
            }

            _pending.Remove(key);

            await InvokeAsync(new PatternWatchWork(pending.Action, key, pending.RemovedPath), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True once two probes a stability window apart agree on size and last-write time, so a file
    /// that is still being written is left alone until the writer is done.
    /// </summary>
    private bool IsWriteStable(string path, Pending pending, DateTime nowUtc)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            // Nothing to wait for; the handler decides what a missing file means.
            return true;
        }

        long length;
        DateTime lastWriteUtc;
        try
        {
            length = info.Length;
            lastWriteUtc = info.LastWriteTimeUtc;
        }
        catch (IOException)
        {
            length = -1;
            lastWriteUtc = DateTime.MinValue;
        }

        if (pending.Probed && pending.LastLength == length && pending.LastWriteUtc == lastWriteUtc)
        {
            return true;
        }

        pending.Probed = true;
        pending.LastLength = length;
        pending.LastWriteUtc = lastWriteUtc;

        if (nowUtc >= pending.DeadlineUtc)
        {
            _logger.LogDebug("Pattern file {FilePath} is still changing after the stability deadline; reloading anyway", path);
            return true;
        }

        pending.DueAtUtc = nowUtc + _options.StabilityWindow;
        return false;
    }

    private async Task InvokeAsync(PatternWatchWork work, CancellationToken cancellationToken)
    {
        try
        {
            await _handler(work, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling pattern watch work {Action} for {FilePath}", work.Action, work.FullPath);
        }
    }

    private static string Canonical(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            // An unusable path is still a key; it just never matches a real file.
            return path;
        }
    }

    private readonly record struct WatchEvent(PatternWatchAction Action, string FullPath, string? OldFullPath);

    private sealed class Pending
    {
        public PatternWatchAction Action { get; set; } = PatternWatchAction.Reload;
        public string? RemovedPath { get; set; }
        public DateTime DueAtUtc { get; set; }
        public DateTime DeadlineUtc { get; set; }
        public bool Probed { get; set; }
        public long LastLength { get; set; } = -1;
        public DateTime LastWriteUtc { get; set; }
    }
}
