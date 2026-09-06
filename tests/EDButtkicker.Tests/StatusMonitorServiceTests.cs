using System.Diagnostics;
using EDButtkicker.Configuration;
using EDButtkicker.Hosting;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The real hosted service driven over a temp folder: Status.json is written between polls and the
/// audio engine is a fake, so no game, no WASAPI and no device are involved. These pin the parts
/// that were wrong or untested when Status.json monitoring landed - the first read staying silent,
/// a Flags2-only edge firing, malformed JSON being skipped, an unmapped event doing nothing, and
/// shutdown not sitting out a hard-coded delay.
/// </summary>
public class StatusMonitorServiceTests : IDisposable
{
    /// <summary>The service polls every 250ms; four of those is a settled baseline.</summary>
    private const int SettleMs = 1000;

    private const long Docked = 1L << 0;
    private const long LandingGearDown = 1L << 2;
    private const long HardpointsDeployed = 1L << 6;
    private const long FsdCharging = 1L << 17;

    private const long LowOxygen2 = 1L << 6;
    private const long InTaxi2 = 1L << 1;

    private readonly TempDirectory _root = new("edbk-status");
    private readonly AppSettings _settings = new();
    private readonly FakeAudioEngine _audio;
    private ServiceProvider? _provider;

    public StatusMonitorServiceTests()
    {
        _settings.EliteDangerous.JournalPath = _root.Path;
        _audio = new FakeAudioEngine(_settings);
    }

    private StatusMonitorService CreateMonitor()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddEliteButtkicker(_settings);

        // Patterns are recorded instead of played, and per-user state stays in the temp folder.
        services.Replace(ServiceDescriptor.Singleton<AudioEngineService>(_audio));
        services.Replace(ServiceDescriptor.Singleton(
            new UserSettingsService(NullLogger<UserSettingsService>.Instance, _root.Path)));

        _provider = services.BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<StatusMonitorService>(_provider);
    }

    private void WriteStatus(long flags, long flags2 = 0) =>
        WriteRaw($$"""{"timestamp":"2026-09-06T12:00:00Z","event":"Status","Flags":{{flags}},"Flags2":{{flags2}}}""");

    private void WriteRaw(string contents) =>
        File.WriteAllText(Path.Combine(_root.Path, "Status.json"), contents);

    private IReadOnlyList<string> PlayedPatterns()
    {
        lock (_audio.Played)
        {
            return _audio.Played.Select(p => p.Name).ToList();
        }
    }

    /// <summary>The pattern name the default mappings give an event, so assertions read by event.</summary>
    private string PatternFor(string eventType) =>
        _provider!.GetRequiredService<EventMappingService>().GetDefaultPatternForEvent(eventType)!.Name;

    [Fact]
    public async Task FirstRead_StoresTheFlagsWithoutFiringAnything()
    {
        // The game is already running with the gear down when monitoring starts. That is state, not
        // an event - firing here would thump the user every time the app launches.
        WriteStatus(Docked | LandingGearDown, LowOxygen2);

        var monitor = CreateMonitor();
        await monitor.StartAsync(CancellationToken.None);

        try
        {
            await Task.Delay(SettleMs);
            Assert.Empty(PlayedPatterns());

            // Proves the monitor really was polling across that window rather than being asleep:
            // the next edge fires, and the baseline it fires from is the state it read first.
            WriteStatus(Docked | LandingGearDown | HardpointsDeployed, LowOxygen2);

            await SetupTestExtensions.WaitForAsync(
                () => PlayedPatterns().Count > 0, "the hardpoints edge to fire");

            Assert.Equal(new[] { PatternFor("HardpointsDeployed") }, PlayedPatterns());
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            monitor.Dispose();
        }
    }

    [Fact]
    public async Task FlagsEdge_FiresTheMappedPatternOnceAndDoesNotRepeatIt()
    {
        WriteStatus(Docked);

        var monitor = CreateMonitor();
        await monitor.StartAsync(CancellationToken.None);

        try
        {
            await Task.Delay(SettleMs);

            WriteStatus(Docked | LandingGearDown);

            await SetupTestExtensions.WaitForAsync(
                () => PlayedPatterns().Count > 0, "the landing gear edge to fire");

            Assert.Equal(new[] { PatternFor("LandingGearDown") }, PlayedPatterns());

            // The same state again is not an edge, whether it is re-written or simply re-polled.
            WriteStatus(Docked | LandingGearDown);
            await Task.Delay(SettleMs);

            Assert.Equal(new[] { PatternFor("LandingGearDown") }, PlayedPatterns());

            // Retracting is a fresh edge, so the monitor is still following the file.
            WriteStatus(Docked);

            await SetupTestExtensions.WaitForAsync(
                () => PlayedPatterns().Count > 1, "the landing gear retraction to fire");

            Assert.Equal(
                new[] { PatternFor("LandingGearDown"), PatternFor("LandingGearUp") },
                PlayedPatterns());
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            monitor.Dispose();
        }
    }

    [Fact]
    public async Task Flags2OnlyEdge_FiresEvenThoughFlagsDidNotChange()
    {
        WriteStatus(Docked | LandingGearDown);

        var monitor = CreateMonitor();
        await monitor.StartAsync(CancellationToken.None);

        try
        {
            await Task.Delay(SettleMs);

            // Flags is byte-for-byte the same; only Flags2 moved.
            WriteStatus(Docked | LandingGearDown, LowOxygen2);

            await SetupTestExtensions.WaitForAsync(
                () => PlayedPatterns().Count > 0, "the Flags2-only low oxygen edge to fire");

            Assert.Equal(new[] { PatternFor("LowOxygen") }, PlayedPatterns());
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            monitor.Dispose();
        }
    }

    [Fact]
    public async Task PartialOrInvalidJson_IsSkippedWithoutThrowingOrFiring()
    {
        WriteStatus(Docked);

        var monitor = CreateMonitor();
        await monitor.StartAsync(CancellationToken.None);

        try
        {
            await Task.Delay(SettleMs);

            // A half-written file - the game truncates and rewrites Status.json in place.
            WriteRaw("""{"timestamp":"2026-09-06T12:00:00Z","event":"Status","Flags":4,"Fla""");
            await Task.Delay(SettleMs);
            Assert.Empty(PlayedPatterns());

            // Not JSON at all, and a Flags value of the wrong shape.
            WriteRaw("not json at all");
            WriteRaw("""{"Flags":"four"}""");
            await Task.Delay(SettleMs);
            Assert.Empty(PlayedPatterns());

            // The monitor survived all of it and still follows the next good write.
            WriteStatus(Docked | LandingGearDown);

            await SetupTestExtensions.WaitForAsync(
                () => PlayedPatterns().Count > 0, "the monitor to recover and fire the gear edge");

            Assert.Equal(new[] { PatternFor("LandingGearDown") }, PlayedPatterns());
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            monitor.Dispose();
        }
    }

    [Fact]
    public async Task EdgeWithNoMappedPattern_IsANoOp()
    {
        WriteStatus(Docked);

        var monitor = CreateMonitor();
        await monitor.StartAsync(CancellationToken.None);

        try
        {
            await Task.Delay(SettleMs);

            // FsdCharging is detected but has no default mapping, and InTaxi has no event at all.
            Assert.Null(_provider!.GetRequiredService<EventMappingService>().GetDefaultPatternForEvent("FsdCharging"));

            WriteStatus(Docked | FsdCharging, InTaxi2);
            await Task.Delay(SettleMs);

            Assert.Empty(PlayedPatterns());

            // Still alive after the unmapped edge.
            WriteStatus(Docked | FsdCharging | LandingGearDown, InTaxi2);

            await SetupTestExtensions.WaitForAsync(
                () => PlayedPatterns().Count > 0, "a mapped edge after the unmapped one to fire");

            Assert.Equal(new[] { PatternFor("LandingGearDown") }, PlayedPatterns());
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            monitor.Dispose();
        }
    }

    [Fact]
    public async Task Shutdown_CompletesPromptlyAndStopsFiring()
    {
        WriteStatus(Docked);

        var monitor = CreateMonitor();
        await monitor.StartAsync(CancellationToken.None);

        try
        {
            await Task.Delay(SettleMs);

            // Stop while a landing gear edge is being picked up. This used to sit out a
            // non-cancellable 2s delay inside the gear handler before the loop could unwind.
            WriteStatus(Docked | LandingGearDown);
            await Task.Delay(300);

            var stopwatch = Stopwatch.StartNew();
            await monitor.StopAsync(CancellationToken.None);
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds < 1000,
                $"Stopping the status monitor took {stopwatch.ElapsedMilliseconds}ms.");

            var playedAtStop = PlayedPatterns().Count;

            // Nothing the game writes afterwards reaches the audio engine.
            WriteStatus(Docked | LandingGearDown | HardpointsDeployed);
            await Task.Delay(SettleMs);

            Assert.Equal(playedAtStop, PlayedPatterns().Count);
        }
        finally
        {
            monitor.Dispose();
        }
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _root.Dispose();
    }
}
