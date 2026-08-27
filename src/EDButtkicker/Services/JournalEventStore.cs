using EDButtkicker.Models;

namespace EDButtkicker.Services;

/// <summary>
/// Bounded, thread-safe history of the journal events the app has seen this session.
/// The web API reads from here, so it has to be a real singleton rather than static state
/// hidden inside a controller.
/// </summary>
public interface IJournalEventStore
{
    void Add(JournalEvent journalEvent);

    /// <summary>Most recent events first, capped at <paramref name="limit"/>.</summary>
    IReadOnlyList<JournalEvent> GetRecent(int limit);

    /// <summary>Events at or after <paramref name="cutoff"/>, oldest first (replay order).</summary>
    IReadOnlyList<JournalEvent> GetSince(DateTime cutoff);

    int Count { get; }

    DateTime? LastTimestamp { get; }
}

public class JournalEventStore : IJournalEventStore
{
    public const int MaxEvents = 1000;

    private readonly List<JournalEvent> _events = new();
    private readonly object _lock = new object();

    public void Add(JournalEvent journalEvent)
    {
        if (journalEvent == null)
            return;

        lock (_lock)
        {
            _events.Insert(0, journalEvent);

            if (_events.Count > MaxEvents)
            {
                _events.RemoveRange(MaxEvents, _events.Count - MaxEvents);
            }
        }
    }

    public IReadOnlyList<JournalEvent> GetRecent(int limit)
    {
        if (limit <= 0)
            return Array.Empty<JournalEvent>();

        lock (_lock)
        {
            return _events
                .OrderByDescending(e => e.Timestamp)
                .Take(limit)
                .ToList();
        }
    }

    public IReadOnlyList<JournalEvent> GetSince(DateTime cutoff)
    {
        lock (_lock)
        {
            return _events
                .Where(e => e.Timestamp >= cutoff)
                .OrderBy(e => e.Timestamp)
                .ToList();
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _events.Count;
            }
        }
    }

    public DateTime? LastTimestamp
    {
        get
        {
            lock (_lock)
            {
                return _events.FirstOrDefault()?.Timestamp;
            }
        }
    }
}
