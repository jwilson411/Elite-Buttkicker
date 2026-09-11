using System.Collections.Concurrent;
using EDButtkicker.Configuration;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The active-effect registry under concurrent use. Playback, the panic stop, the scheduled cleanup
/// of a finished effect and a device change all mutate the same bookkeeping from different threads,
/// so the invariant tested here is that they never leave an entry behind, never tear the same effect
/// down twice, and never surface an exception to the caller. The output is a seam, so this runs on a
/// machine with no audio device at all - it proves the bookkeeping, not that anything was heard.
/// </summary>
public class AudioEffectRegistryTests
{
    [Fact]
    public async Task ConcurrentPlayStopAndReinitialize_LeaveTheRegistryEmpty()
    {
        var logger = new RecordingLogger();
        using var engine = CreateEngine(logger);
        engine.Initialize();

        // Effects long enough to still be playing while the stops and device changes run, so the
        // stop paths really do race the registration path rather than tidying up after it.
        var work = new List<Task>();
        for (int i = 0; i < 24; i++)
        {
            work.Add(Task.Run(() => engine.TryPlayHapticPattern(TestPattern(durationMs: 400))));
        }

        for (int i = 0; i < 4; i++)
        {
            work.Add(Task.Run(() => engine.StopAllEffects()));
        }

        for (int i = 0; i < 2; i++)
        {
            work.Add(Task.Run(() => engine.Reinitialize()));
        }

        await Task.WhenAll(work);

        // Whatever survived the last stop is ramped out here; nothing is playing afterwards.
        engine.StopAllEffects();

        Assert.Equal(0, engine.GetStatus().ActiveEffects);
        AssertNoFailuresLogged(logger);

        // The scheduled cleanups outlive the effects they were started for; none of them may fault
        // on a registry entry that a stop or a reinitialize already removed.
        await Task.Delay(600);

        Assert.Equal(0, engine.GetStatus().ActiveEffects);
        AssertNoFailuresLogged(logger);
    }

    [Fact]
    public async Task StopsRacingTheScheduledCleanup_NeverTearTheSameEffectDownTwice()
    {
        var logger = new RecordingLogger();
        using var engine = CreateEngine(logger);
        engine.Initialize();

        // Short effects: each schedules its own cleanup ~100ms out, so the stops below keep landing
        // on effects that are about to be - or have just been - cleaned up by their own timer.
        var deadline = DateTime.UtcNow.AddMilliseconds(400);

        var playing = Task.Run(async () =>
        {
            while (DateTime.UtcNow < deadline)
            {
                await engine.TryPlayHapticPattern(TestPattern(durationMs: 0));
                await Task.Delay(5);
            }
        });

        var stopping = Task.Run(() =>
        {
            while (DateTime.UtcNow < deadline)
            {
                engine.StopAllEffects();
            }
        });

        await Task.WhenAll(playing, stopping);

        engine.StopAllEffects();
        await Task.Delay(300); // outlives the last scheduled cleanup

        Assert.Equal(0, engine.GetStatus().ActiveEffects);
        AssertNoFailuresLogged(logger);
    }

    [Fact]
    public async Task AnEffectThatFinishesOnItsOwn_IsRemovedAndCanStillBeStoppedAndDisposed()
    {
        var logger = new RecordingLogger();
        var engine = CreateEngine(logger);
        engine.Initialize();

        var result = await engine.TryPlayHapticPattern(TestPattern(durationMs: 0));
        Assert.True(result.Played, result.Error);
        Assert.Equal(1, engine.GetStatus().ActiveEffects);

        // The scheduled cleanup is the only thing that removes this one.
        await SetupTestExtensions.WaitForAsync(
            () => engine.GetStatus().ActiveEffects == 0,
            "the scheduled cleanup to remove the finished effect",
            timeoutMs: 5000);

        // Stopping and disposing an effect that has already been cleaned up must not cancel or
        // dispose its cancellation source a second time.
        Assert.Equal(0, engine.StopAllEffects());
        Assert.Equal(0, engine.StopAllEffects());
        engine.Dispose();
        engine.Dispose();

        Assert.Equal(0, engine.GetStatus().ActiveEffects);
        AssertNoFailuresLogged(logger);
    }

    [Fact]
    public async Task ReinitializeWhileEffectsArePlaying_ClearsThemAndKeepsPlaybackWorking()
    {
        var logger = new RecordingLogger();
        using var engine = CreateEngine(logger);
        engine.Initialize();

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => engine.TryPlayHapticPattern(TestPattern(durationMs: 2000))));

        Assert.Equal(8, engine.GetStatus().ActiveEffects);

        engine.Reinitialize();

        Assert.Equal(0, engine.GetStatus().ActiveEffects);
        Assert.True(engine.GetStatus().Initialized);

        // The new device is usable: the registry was emptied, not broken.
        var afterwards = await engine.TryPlayHapticPattern(TestPattern(durationMs: 400));
        Assert.True(afterwards.Played, afterwards.Error);
        Assert.Equal(1, engine.GetStatus().ActiveEffects);

        Assert.Equal(1, engine.StopAllEffects());
        Assert.Equal(0, engine.GetStatus().ActiveEffects);
        AssertNoFailuresLogged(logger);
    }

    private static AudioEngineService CreateEngine(RecordingLogger logger) => new(
        logger,
        new AppSettings(),
        FakeAudioDeviceCatalog.With("ButtKicker Amp"),
        new PlayingOutputFactory());

    private static HapticPattern TestPattern(int durationMs) => new()
    {
        Name = "Registry Test",
        Pattern = PatternType.SustainedRumble,
        Frequency = 40,
        Duration = durationMs,
        Intensity = 50,
        FadeOut = 0
    };

    /// <summary>
    /// Nothing in the registry paths may report a failure. A device that has been reinitialized out
    /// from under a caller is allowed to refuse playback, so the bar is that no log entry carries an
    /// exception - that is what a torn registry or a double dispose would look like.
    /// </summary>
    private static void AssertNoFailuresLogged(RecordingLogger logger)
    {
        var failures = logger.Failures;
        Assert.True(failures.Count == 0, "Exceptions were logged: " + string.Join(" | ", failures));
    }

    /// <summary>Keeps every log entry that carried an exception, formatted for the assertion message.</summary>
    private sealed class RecordingLogger : ILogger<AudioEngineService>
    {
        private readonly ConcurrentQueue<string> _failures = new();

        public IReadOnlyList<string> Failures => _failures.ToList();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception != null)
            {
                _failures.Enqueue($"{formatter(state, exception)} ({exception.GetType().Name}: {exception.Message})");
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    /// <summary>An output that reports itself as playing and makes no sound.</summary>
    private sealed class PlayingOutputFactory : IAudioOutputFactory
    {
        public AudioOutputHandle OpenEndpoint(string endpointId) =>
            new(new SilentWavePlayer(), "FakeOut", endpointId, "Fake ButtKicker Amp");

        public AudioOutputHandle OpenDefault() =>
            new(new SilentWavePlayer(), "FakeOut", null, "Fake Default Device");
    }

    private sealed class SilentWavePlayer : IWavePlayer
    {
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;

        public float Volume { get; set; } = 1f;

        public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;

        public WaveFormat? OutputWaveFormat { get; private set; }

        public void Init(IWaveProvider waveProvider) => OutputWaveFormat = waveProvider.WaveFormat;

        public void Play() => PlaybackState = PlaybackState.Playing;

        public void Pause() => PlaybackState = PlaybackState.Paused;

        public void Stop()
        {
            PlaybackState = PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs());
        }

        public void Dispose() => PlaybackState = PlaybackState.Stopped;
    }
}
