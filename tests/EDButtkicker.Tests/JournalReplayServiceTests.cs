using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Replay used to be cancelled by blocking on the replay task while holding the lock the replay
/// itself needed, and it handed every event over on a fixed 500 ms tick. These pin the two things
/// that had to change: cancel/restart/shutdown never wait on a thread, and the spacing between
/// events comes from the journal's own timestamps - capped, so one hole cannot stall a replay.
/// </summary>
public class JournalReplayServiceTests
{
    /// <summary>Every wait here is a race guard, not a sleep: a hang fails the test instead of it.</summary>
    private static readonly TimeSpan NoHang = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task StopAsync_DuringAReplay_StopsFeedingThePipeline()
    {
        var pipeline = new RecordingJournalPipeline();
        using var service = NewService(pipeline);

        Assert.True(await service.StartAsync(Events("A", 6, TimeSpan.FromSeconds(2))));
        await SetupTestExtensions.WaitForAsync(() => pipeline.Processed.Count > 0, "the replay to start");

        await service.StopAsync().WaitAsync(NoHang);

        var processedAtStop = pipeline.Processed.Count;
        Assert.InRange(processedAtStop, 1, 5);
        Assert.False(service.GetStatus().IsReplaying);

        // Well past the capped gap the cancelled run was sitting in.
        await Task.Delay(200);

        Assert.Equal(processedAtStop, pipeline.Processed.Count);
    }

    [Fact]
    public async Task StopAsync_WithNothingReplaying_Returns()
    {
        var pipeline = new RecordingJournalPipeline();
        using var service = NewService(pipeline);

        await service.StopAsync().WaitAsync(NoHang);

        Assert.False(service.GetStatus().IsReplaying);
        Assert.Empty(pipeline.Processed);
    }

    [Fact]
    public async Task StartAsync_WhileAReplayIsRunning_CancelsTheOldRunBeforeTheNewOneBegins()
    {
        var pipeline = new RecordingJournalPipeline();
        using var service = NewService(pipeline);

        await service.StartAsync(Events("Old", 10, TimeSpan.FromSeconds(2)));
        await SetupTestExtensions.WaitForAsync(
            () => pipeline.Processed.Count > 0, "the first replay to start");

        // The restart drains the previous run rather than blocking on it: a hang here is the
        // deadlock this service exists to rule out.
        await service.StartAsync(Events("New", 3, TimeSpan.Zero), source: "new.log").WaitAsync(NoHang);

        await SetupTestExtensions.WaitForAsync(
            () => pipeline.Processed.Count(e => e.Event.StartsWith("New")) == 3, "the second replay to finish");

        var processed = pipeline.Processed.ToList();
        var firstNew = processed.FindIndex(e => e.Event.StartsWith("New"));

        Assert.Contains(processed, e => e.Event.StartsWith("Old"));
        // Nothing from the cancelled run reached the pipeline once the new run had started.
        Assert.All(processed.Skip(firstNew), e => Assert.StartsWith("New", e.Event));
        Assert.Equal("new.log", service.GetStatus().Source);
    }

    [Fact]
    public async Task DisposeAsync_WhileAReplayIsInFlight_Completes()
    {
        var pipeline = new RecordingJournalPipeline();
        var service = NewService(pipeline);

        await service.StartAsync(Events("A", 10, TimeSpan.FromSeconds(2)));
        await SetupTestExtensions.WaitForAsync(() => pipeline.Processed.Count > 0, "the replay to start");

        await service.DisposeAsync().AsTask().WaitAsync(NoHang);

        Assert.False(service.GetStatus().IsReplaying);
    }

    [Fact]
    public async Task StartAsync_AfterDisposal_DoesNotReplay()
    {
        var pipeline = new RecordingJournalPipeline();
        var service = NewService(pipeline);

        await service.DisposeAsync();

        Assert.False(await service.StartAsync(Events("A", 3, TimeSpan.Zero)).WaitAsync(NoHang));
        Assert.Empty(pipeline.Processed);
    }

    [Fact]
    public async Task StartAsync_SpacesEventsByTheirOwnTimestamps()
    {
        var pipeline = new RecordingJournalPipeline();
        using var service = NewService(pipeline);

        await service.StartAsync(Events("A", 3, TimeSpan.FromSeconds(1)));
        await SetupTestExtensions.WaitForAsync(() => pipeline.Processed.Count > 0, "the replay to start");

        // A second apart in the journal is a second apart in the replay, not the old fixed tick.
        await Task.Delay(200);

        Assert.Single(pipeline.Processed);

        await service.StopAsync().WaitAsync(NoHang);
    }

    [Fact]
    public async Task StartAsync_DoesNotStallOnAnHourLongHoleInTheJournal()
    {
        var pipeline = new RecordingJournalPipeline();
        using var service = NewService(pipeline);

        await service.StartAsync(Events("A", 2, TimeSpan.FromHours(1)));

        // Only the cap makes this finish: replayed verbatim it would be an hour.
        await SetupTestExtensions.WaitForAsync(
            () => pipeline.Processed.Count == 2, "the capped gap to elapse", timeoutMs: 5000);
    }

    [Fact]
    public async Task StartAsync_WithNoEvents_ReportsNothingStarted()
    {
        var pipeline = new RecordingJournalPipeline();
        using var service = NewService(pipeline);

        Assert.False(await service.StartAsync(Array.Empty<JournalEvent>()));
        Assert.False(service.GetStatus().IsReplaying);
    }

    [Theory]
    [InlineData(0, 500, 1.0, 500)]      // the source's own spacing
    [InlineData(0, 3_600_000, 1.0, 2000)] // an hours-long hole, capped
    [InlineData(500, 0, 1.0, 0)]        // a backwards clock step never waits
    [InlineData(0, 2000, 4.0, 500)]     // accelerated
    [InlineData(0, 2000, 1000.0, 125)]  // speed is clamped, so it stays a replay
    public void DelayBetween_IsTheCappedGapDividedBySpeed(
        int currentMs, int nextMs, double speed, int expectedMs)
    {
        var origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var delay = JournalReplayService.DelayBetween(
            origin.AddMilliseconds(currentMs), origin.AddMilliseconds(nextMs), speed);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), delay);
    }

    private static JournalReplayService NewService(RecordingJournalPipeline pipeline) =>
        new(NullLogger<JournalReplayService>.Instance, pipeline);

    /// <summary>Events named so a replayed event can be traced back to the run that queued it.</summary>
    private static List<JournalEvent> Events(string prefix, int count, TimeSpan spacing)
    {
        var origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        return Enumerable.Range(0, count)
            .Select(i => new JournalEvent
            {
                Timestamp = origin + spacing * i,
                Event = $"{prefix}{i}",
                StarSystem = "Shinrarta Dezhra"
            })
            .ToList();
    }
}
