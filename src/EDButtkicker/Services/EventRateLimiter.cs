using System.Collections.Concurrent;

namespace EDButtkicker.Services;

/// <summary>
/// Per-event-type rate limit that keeps bursty journal events (hull damage, being under attack)
/// from flooding the transducer. Time comes from an injected <see cref="TimeProvider"/> so the
/// windows are deterministic in tests instead of depending on wall clock timing.
/// </summary>
public sealed class EventRateLimiter
{
    /// <summary>Minimum spacing between two accepted occurrences of the same event type.</summary>
    public static readonly IReadOnlyDictionary<string, TimeSpan> DefaultLimits =
        new Dictionary<string, TimeSpan>
        {
            ["HullDamage"] = TimeSpan.FromMilliseconds(500),
            ["ShipTargeted"] = TimeSpan.FromMilliseconds(1000),
            ["FuelScoop"] = TimeSpan.FromSeconds(2),
            ["HeatWarning"] = TimeSpan.FromSeconds(1),
            ["HeatDamage"] = TimeSpan.FromMilliseconds(800),
            ["UnderAttack"] = TimeSpan.FromMilliseconds(300),
            ["Touchdown"] = TimeSpan.FromSeconds(3),
            ["Liftoff"] = TimeSpan.FromSeconds(3),
            ["ShieldDown"] = TimeSpan.FromSeconds(2),
            ["ShieldsUp"] = TimeSpan.FromSeconds(2)
        };

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAccepted = new();
    private readonly IReadOnlyDictionary<string, TimeSpan> _limits;
    private readonly TimeProvider _timeProvider;

    public EventRateLimiter(TimeProvider? timeProvider = null, IReadOnlyDictionary<string, TimeSpan>? limits = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _limits = limits ?? DefaultLimits;
    }

    /// <summary>
    /// True when <paramref name="eventType"/> may be played now; the acceptance is recorded so the
    /// next occurrence inside the window is refused. Event types without a limit are always accepted.
    /// </summary>
    public bool TryAcquire(string eventType)
    {
        if (string.IsNullOrEmpty(eventType))
            return false;

        var now = _timeProvider.GetUtcNow();

        if (!_limits.TryGetValue(eventType, out var minInterval))
        {
            _lastAccepted[eventType] = now;
            return true;
        }

        // AddOrUpdate keeps the check-and-record atomic, so two threads racing on the same event
        // type can never both be accepted inside one window.
        var accepted = false;
        _lastAccepted.AddOrUpdate(
            eventType,
            _ =>
            {
                accepted = true;
                return now;
            },
            (_, last) =>
            {
                if (now - last < minInterval)
                    return last;

                accepted = true;
                return now;
            });

        return accepted;
    }

    /// <summary>Last time an occurrence of <paramref name="eventType"/> was accepted, if any.</summary>
    public DateTimeOffset? LastAccepted(string eventType) =>
        _lastAccepted.TryGetValue(eventType, out var last) ? last : null;

    public void Reset() => _lastAccepted.Clear();
}
