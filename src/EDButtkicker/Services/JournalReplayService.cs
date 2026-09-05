using EDButtkicker.Models;
using Microsoft.Extensions.Logging;

namespace EDButtkicker.Services;

/// <summary>What the replay API reports about the run this process owns right now.</summary>
public sealed record JournalReplayStatus(
    bool IsReplaying,
    int TotalEvents,
    int EventsReplayed,
    string? Source,
    double Speed);

/// <summary>
/// Owns the lifetime of a journal replay: the cancellation source, the running task and the status
/// the API reports. It lives here rather than in the controller because a replay outlives the
/// request that started it - the request thread must be able to hand it over and return, and the
/// next request must be able to cancel it without ever blocking on it.
/// </summary>
public sealed class JournalReplayService : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// The longest wait between two replayed events. Journals contain hours-long holes (the pilot
    /// walked away); replaying those verbatim would look like a hung replay, so the gap is capped.
    /// </summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(2);

    /// <summary>Playback speed bounds: slower than a quarter or faster than 16x is not a replay.</summary>
    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 16.0;
    public const double DefaultSpeed = 1.0;

    private readonly ILogger<JournalReplayService> _logger;
    private readonly IJournalEventPipeline _pipeline;

    /// <summary>
    /// Serializes start/stop. It is an async gate, not a monitor, so the awaits inside - cancelling
    /// and draining the previous run - never hold a thread, and a request can be aborted while it
    /// waits its turn.
    /// </summary>
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    /// <summary>Guards the fields below for the readers (status) that never take <see cref="_lifecycle"/>.</summary>
    private readonly object _state = new();

    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private int _totalEvents;
    private int _eventsReplayed;
    private string? _source;
    private double _speed = DefaultSpeed;
    private volatile bool _disposed;

    public JournalReplayService(ILogger<JournalReplayService> logger, IJournalEventPipeline pipeline)
    {
        _logger = logger;
        _pipeline = pipeline;
    }

    /// <summary>
    /// Cancels whatever is replaying, waits for it to unwind, and starts <paramref name="events"/>.
    /// The previous run is fully drained before the new one is created, so no event from the old
    /// run can reach the pipeline after this returns. Returns false when nothing was started.
    /// </summary>
    /// <param name="requestAborted">
    /// The caller's cancellation - only aborts the wait for the gate, never the replay itself.
    /// </param>
    public async Task<bool> StartAsync(
        IReadOnlyList<JournalEvent> events,
        double speed = DefaultSpeed,
        string? source = null,
        CancellationToken requestAborted = default)
    {
        if (events.Count == 0)
        {
            return false;
        }

        speed = Math.Clamp(speed, MinSpeed, MaxSpeed);

        await _lifecycle.WaitAsync(requestAborted).ConfigureAwait(false);
        try
        {
            await CancelCurrentAsync().ConfigureAwait(false);

            if (_disposed)
            {
                _logger.LogWarning("Journal replay was requested after shutdown had begun; not starting");
                return false;
            }

            // Copy the caller's list: the replay outlives this call and must not see it mutate.
            var snapshot = events.ToArray();
            var cancellation = new CancellationTokenSource();
            var token = cancellation.Token;

            lock (_state)
            {
                _cancellation = cancellation;
                _totalEvents = snapshot.Length;
                _eventsReplayed = 0;
                _source = source;
                _speed = speed;
                _task = Task.Run(() => RunAsync(snapshot, speed, token), CancellationToken.None);
            }

            _logger.LogInformation(
                "Started journal replay of {Count} events from {Source} at {Speed}x",
                snapshot.Length, source ?? "recent events", speed);

            return true;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Cancels the running replay and waits for it to finish. Safe to call when nothing is running,
    /// and safe to call from a request thread: every wait here is an await.
    /// </summary>
    public async Task StopAsync(CancellationToken requestAborted = default)
    {
        await _lifecycle.WaitAsync(requestAborted).ConfigureAwait(false);
        try
        {
            await CancelCurrentAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public JournalReplayStatus GetStatus()
    {
        lock (_state)
        {
            var replaying = _task is { IsCompleted: false } && _cancellation is { IsCancellationRequested: false };

            return new JournalReplayStatus(replaying, _totalEvents, Volatile.Read(ref _eventsReplayed), _source, _speed);
        }
    }

    /// <summary>
    /// The wait between two events: the source's own spacing, capped so one hole in the journal
    /// cannot stall the replay, and divided by the playback speed.
    /// </summary>
    public static TimeSpan DelayBetween(DateTime current, DateTime next, double speed = DefaultSpeed)
    {
        var gap = next - current;

        // Journals are written in order, but a clock change can still produce a backwards step.
        if (gap <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (gap > MaxGap)
        {
            gap = MaxGap;
        }

        return gap / Math.Clamp(speed, MinSpeed, MaxSpeed);
    }

    /// <summary>
    /// Takes the running replay out of the fields under a short lock, then cancels and awaits it
    /// with no lock held - a replay that needs to touch this service while unwinding cannot deadlock
    /// against its own canceller. Callers must hold <see cref="_lifecycle"/>.
    /// </summary>
    private async Task CancelCurrentAsync()
    {
        CancellationTokenSource? cancellation;
        Task? task;

        lock (_state)
        {
            cancellation = _cancellation;
            task = _task;
            _cancellation = null;
            _task = null;
        }

        if (cancellation == null)
        {
            return;
        }

        cancellation.Cancel();

        if (task != null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the run was cancelled between events or inside its delay.
            }
        }

        cancellation.Dispose();

        _logger.LogInformation("Journal replay stopped");
    }

    private async Task RunAsync(IReadOnlyList<JournalEvent> events, double speed, CancellationToken cancellationToken)
    {
        try
        {
            for (var i = 0; i < events.Count; i++)
            {
                // Checked before every event as well as inside the delay, so a cancel lands even
                // when consecutive events carry the same timestamp and the delay is zero.
                cancellationToken.ThrowIfCancellationRequested();

                // Same ordered pipeline as live monitoring, minus the history write - these
                // events are historical and (for the in-memory source) already in the store.
                await _pipeline.ProcessAsync(events[i], skipHistory: true).ConfigureAwait(false);
                Interlocked.Increment(ref _eventsReplayed);

                _logger.LogDebug("Replayed event: {EventType} at {Timestamp}", events[i].Event, events[i].Timestamp);

                if (i + 1 < events.Count)
                {
                    var delay = DelayBetween(events[i].Timestamp, events[i + 1].Timestamp, speed);
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            _logger.LogInformation("Journal replay completed after {Count} events", events.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Journal replay was cancelled after {Count} events", Volatile.Read(ref _eventsReplayed));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during journal replay");
        }
    }

    /// <summary>
    /// Synchronous disposal only asks the replay to stop - it never blocks on it, because the
    /// callers of this path (a service provider being torn down, a test fixture) may be on a thread
    /// the replay itself needs.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;

        lock (_state)
        {
            _cancellation?.Cancel();
        }
    }

    /// <summary>The shutdown path the host uses: cancel, then wait for the replay to unwind.</summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        await StopAsync().ConfigureAwait(false);

        // The gate is deliberately not disposed: a start racing this shutdown would then fail with
        // ObjectDisposedException instead of simply finding the service closed.
    }
}
