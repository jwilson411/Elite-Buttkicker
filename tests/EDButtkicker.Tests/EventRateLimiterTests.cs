using EDButtkicker.Services;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The per-event-type rate limit that stops bursty journal events from flooding the transducer.
/// Every window here is driven by a hand-advanced clock, so nothing waits on the wall clock.
/// </summary>
public class EventRateLimiterTests
{
    [Fact]
    public void FirstOccurrence_IsAlwaysAccepted()
    {
        var limiter = new EventRateLimiter(new ManualTimeProvider());

        Assert.True(limiter.TryAcquire("UnderAttack"));
    }

    [Fact]
    public void SecondOccurrenceInsideTheWindow_IsRefused()
    {
        var clock = new ManualTimeProvider();
        var limiter = new EventRateLimiter(clock);

        Assert.True(limiter.TryAcquire("UnderAttack"));

        clock.Advance(TimeSpan.FromMilliseconds(299)); // window is 300ms
        Assert.False(limiter.TryAcquire("UnderAttack"));
    }

    [Fact]
    public void OccurrenceExactlyAtTheWindowBoundary_IsAccepted()
    {
        var clock = new ManualTimeProvider();
        var limiter = new EventRateLimiter(clock);

        Assert.True(limiter.TryAcquire("UnderAttack"));

        clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert.True(limiter.TryAcquire("UnderAttack"));
    }

    [Fact]
    public void RefusedOccurrences_DoNotExtendTheWindow()
    {
        var clock = new ManualTimeProvider();
        var limiter = new EventRateLimiter(clock);

        Assert.True(limiter.TryAcquire("HullDamage")); // 500ms window

        // A burst of refused events must not push the next acceptance further out.
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(limiter.TryAcquire("HullDamage"));
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(limiter.TryAcquire("HullDamage"));

        clock.Advance(TimeSpan.FromMilliseconds(100)); // 500ms after the accepted one
        Assert.True(limiter.TryAcquire("HullDamage"));
    }

    [Fact]
    public void WindowsAreTrackedPerEventType()
    {
        var clock = new ManualTimeProvider();
        var limiter = new EventRateLimiter(clock);

        Assert.True(limiter.TryAcquire("HullDamage"));
        Assert.True(limiter.TryAcquire("ShieldDown"));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.False(limiter.TryAcquire("HullDamage"));
        Assert.False(limiter.TryAcquire("ShieldDown"));

        clock.Advance(TimeSpan.FromMilliseconds(500)); // clears HullDamage (500ms), not ShieldDown (2s)
        Assert.True(limiter.TryAcquire("HullDamage"));
        Assert.False(limiter.TryAcquire("ShieldDown"));
    }

    [Fact]
    public void EventTypesWithoutALimit_AreNeverRefused()
    {
        var clock = new ManualTimeProvider();
        var limiter = new EventRateLimiter(clock);

        for (var i = 0; i < 50; i++)
        {
            Assert.True(limiter.TryAcquire("Docked"));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingEventName_IsRefused(string? eventType)
    {
        var limiter = new EventRateLimiter(new ManualTimeProvider());

        Assert.False(limiter.TryAcquire(eventType!));
    }

    [Fact]
    public void Reset_ClearsEveryWindow()
    {
        var clock = new ManualTimeProvider();
        var limiter = new EventRateLimiter(clock);

        Assert.True(limiter.TryAcquire("Touchdown")); // 3s window
        Assert.False(limiter.TryAcquire("Touchdown"));

        limiter.Reset();

        Assert.Null(limiter.LastAccepted("Touchdown"));
        Assert.True(limiter.TryAcquire("Touchdown"));
    }

    [Fact]
    public void CustomLimits_ReplaceTheDefaults()
    {
        var clock = new ManualTimeProvider();
        var limiter = new EventRateLimiter(clock, new Dictionary<string, TimeSpan>
        {
            ["Docked"] = TimeSpan.FromSeconds(10)
        });

        Assert.True(limiter.TryAcquire("Docked"));
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(limiter.TryAcquire("Docked"));

        // A type limited by default is unlimited under this configuration.
        Assert.True(limiter.TryAcquire("HullDamage"));
        Assert.True(limiter.TryAcquire("HullDamage"));
    }

    [Fact]
    public void LastAccepted_TracksTheAcceptedOccurrenceOnly()
    {
        var clock = new ManualTimeProvider();
        var limiter = new EventRateLimiter(clock);
        var accepted = clock.GetUtcNow();

        Assert.True(limiter.TryAcquire("HeatDamage"));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.False(limiter.TryAcquire("HeatDamage"));

        Assert.Equal(accepted, limiter.LastAccepted("HeatDamage"));
    }

    [Fact]
    public void ConcurrentBurst_AcceptsExactlyOneEventPerWindow()
    {
        var limiter = new EventRateLimiter(new ManualTimeProvider());
        var accepted = 0;

        // Overlapping journal reads can process the same burst from several threads.
        Parallel.For(0, 64, _ =>
        {
            if (limiter.TryAcquire("UnderAttack"))
                Interlocked.Increment(ref accepted);
        });

        Assert.Equal(1, accepted);
    }
}
