using EDButtkicker.Configuration;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Xunit;

namespace EDButtkicker.Tests;

// CA1416: VoiceFeedbackService is marked Windows-only because System.Speech is, but the cue
// playback under test is platform-neutral - it goes through the audio output seam, and the service
// keeps working with no synthesizer. Suppressed for the file so these tests run on the CI agent
// rather than only on a Windows box.
#pragma warning disable CA1416

/// <summary>
/// Audio cues have to complete on the device's own PlaybackStopped callback, play out of the
/// configured endpoint, and stop when the service goes away. Every device here is a fake, so this
/// runs on a machine with no audio hardware at all.
/// </summary>
public class VoiceFeedbackServiceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AMissingCueFile_ReturnsWithoutOpeningAnOutput()
    {
        var factory = new FakeAudioOutputFactory();
        using var service = CreateService(new AppSettings(), factory);

        await service.PlayAudioCue($"not-a-file-{Guid.NewGuid():N}.wav").WaitAsync(Patience);

        Assert.Empty(factory.OpenedEndpoints);
        Assert.Equal(0, factory.OpenDefaultCalls);
    }

    /// <summary>
    /// The point of the rewrite: playback finishes because the device said it finished. While the
    /// device is still playing the call is outstanding, and it is PlaybackStopped - not a timer and
    /// not a polling loop - that completes it.
    /// </summary>
    [Fact]
    public async Task CuePlayback_CompletesWhenTheDeviceRaisesPlaybackStopped()
    {
        using var cue = new TempCueFile();
        var player = new FakeWavePlayer();
        var factory = new FakeAudioOutputFactory(() => player);
        using var service = CreateService(new AppSettings(), factory);

        var playback = service.PlayAudioCue(cue.FileName);

        Assert.Equal(1, player.PlayCalls);
        Assert.False(playback.IsCompleted, "playback should still be outstanding while the device is playing");

        player.RaisePlaybackStopped();
        await playback.WaitAsync(Patience);

        Assert.True(player.Disposed, "the output should be disposed once the cue has finished");
    }

    /// <summary>
    /// A device failure is the service's to log, not the caller's to catch: the task completes
    /// either way, so a bad cue cannot take down the pattern that triggered it.
    /// </summary>
    [Fact]
    public async Task APlaybackFailure_CompletesTheCallRatherThanThrowing()
    {
        using var cue = new TempCueFile();
        var player = new FakeWavePlayer();
        var factory = new FakeAudioOutputFactory(() => player);
        using var service = CreateService(new AppSettings(), factory);

        var playback = service.PlayAudioCue(cue.FileName);
        player.RaisePlaybackStopped(new InvalidOperationException("the device went away"));

        await playback.WaitAsync(Patience);

        Assert.True(player.Disposed);
    }

    /// <summary>The saved endpoint is opened by id, the same way haptic patterns open it.</summary>
    [Fact]
    public async Task CuePlayback_OpensTheConfiguredEndpoint()
    {
        using var cue = new TempCueFile();
        var settings = new AppSettings();
        settings.Audio.AudioDeviceEndpointId = FakeAudioDeviceCatalog.EndpointIdFor(0);
        settings.Audio.AudioDeviceName = "Buttkicker";

        var factory = new FakeAudioOutputFactory(() => new FakeWavePlayer(stopWhenPlayed: true));
        using var service = CreateService(settings, factory, FakeAudioDeviceCatalog.With("Buttkicker"));

        await service.PlayAudioCue(cue.FileName).WaitAsync(Patience);

        Assert.Equal(new[] { FakeAudioDeviceCatalog.EndpointIdFor(0) }, factory.OpenedEndpoints);
        Assert.Equal(0, factory.OpenDefaultCalls);
    }

    /// <summary>A saved device that is no longer enumerated must not silence the cue.</summary>
    [Fact]
    public async Task AnEndpointThatIsGone_FallsBackToTheDefaultOutput()
    {
        using var cue = new TempCueFile();
        var settings = new AppSettings();
        settings.Audio.AudioDeviceEndpointId = FakeAudioDeviceCatalog.EndpointIdFor(7);
        settings.Audio.AudioDeviceName = "Unplugged Buttkicker";

        var factory = new FakeAudioOutputFactory(() => new FakeWavePlayer(stopWhenPlayed: true));
        using var service = CreateService(settings, factory, FakeAudioDeviceCatalog.With("Buttkicker"));

        await service.PlayAudioCue(cue.FileName).WaitAsync(Patience);

        Assert.Empty(factory.OpenedEndpoints);
        Assert.Equal(1, factory.OpenDefaultCalls);
    }

    /// <summary>Disposing the service stops whatever is still sounding and releases the caller.</summary>
    [Fact]
    public async Task Disposing_CancelsACueThatIsStillPlaying()
    {
        using var cue = new TempCueFile();
        var player = new FakeWavePlayer();
        var factory = new FakeAudioOutputFactory(() => player);
        var service = CreateService(new AppSettings(), factory);

        var playback = service.PlayAudioCue(cue.FileName);
        Assert.False(playback.IsCompleted);

        service.Dispose();

        await playback.WaitAsync(Patience);
        Assert.True(player.StopCalls >= 1, "the in-flight cue should have been stopped");
        Assert.True(player.Disposed);
    }

    /// <summary>
    /// The thread-cost regression test. Every one of these cues is still sounding at the same time,
    /// so the old implementation - a Task.Run parked in Thread.Sleep until playback ended - would
    /// hold one pool thread each and take the pool's thread-injection rate (roughly one or two a
    /// second past the minimum) to get the last of them as far as Play. Completing on a callback
    /// instead costs no thread, so they all start at once.
    /// </summary>
    [Fact]
    public async Task ManyConcurrentCues_AllReachPlaybackWithoutHoldingPoolThreads()
    {
        const int cueCount = 200;

        using var cue = new TempCueFile();
        var players = new List<FakeWavePlayer>();
        var factory = new FakeAudioOutputFactory(() =>
        {
            var player = new FakeWavePlayer();
            lock (players) { players.Add(player); }
            return player;
        });

        using var service = CreateService(new AppSettings(), factory);

        var playbacks = Enumerable.Range(0, cueCount)
            .Select(_ => service.PlayAudioCue(cue.FileName))
            .ToList();

        FakeWavePlayer[] started;
        lock (players) { started = players.ToArray(); }

        Assert.Equal(cueCount, started.Length);
        Assert.All(started, player => Assert.Equal(1, player.PlayCalls));
        Assert.All(playbacks, playback => Assert.False(playback.IsCompleted));

        foreach (var player in started)
        {
            player.RaisePlaybackStopped();
        }

        await Task.WhenAll(playbacks).WaitAsync(Patience);
    }

    private static VoiceFeedbackService CreateService(
        AppSettings settings,
        IAudioOutputFactory factory,
        IAudioDeviceCatalog? catalog = null) =>
        new(NullLogger<VoiceFeedbackService>.Instance, settings, catalog, factory);

    /// <summary>A real, readable WAV file where the service looks for cues, deleted afterwards.</summary>
    private sealed class TempCueFile : IDisposable
    {
        private readonly string _path;

        public TempCueFile()
        {
            var directory = Path.Combine(Directory.GetCurrentDirectory(), "audio_cues");
            Directory.CreateDirectory(directory);

            FileName = $"cue-{Guid.NewGuid():N}.wav";
            _path = Path.Combine(directory, FileName);

            using var writer = new WaveFileWriter(_path, new WaveFormat(44100, 16, 1));
            writer.WriteSamples(new short[4410], 0, 4410);
        }

        /// <summary>The name as a caller passes it, relative to the audio_cues directory.</summary>
        public string FileName { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
                // A leftover file in the test output directory is not worth failing a test over.
            }
        }
    }

    /// <summary>Hands out players the test controls, and records what it was asked to open.</summary>
    private sealed class FakeAudioOutputFactory : IAudioOutputFactory
    {
        private readonly Func<FakeWavePlayer> _players;
        private readonly List<string> _openedEndpoints = new();
        private int _openDefaultCalls;

        public FakeAudioOutputFactory(Func<FakeWavePlayer>? players = null)
        {
            _players = players ?? (() => new FakeWavePlayer(stopWhenPlayed: true));
        }

        public IReadOnlyList<string> OpenedEndpoints
        {
            get { lock (_openedEndpoints) { return _openedEndpoints.ToArray(); } }
        }

        public int OpenDefaultCalls => Volatile.Read(ref _openDefaultCalls);

        public AudioOutputHandle OpenEndpoint(string endpointId)
        {
            lock (_openedEndpoints) { _openedEndpoints.Add(endpointId); }
            return new AudioOutputHandle(_players(), "FakeWasapi", endpointId, "Fake Endpoint");
        }

        public AudioOutputHandle OpenDefault()
        {
            Interlocked.Increment(ref _openDefaultCalls);
            return new AudioOutputHandle(_players(), "FakeDefault", null, "Fake Default Device");
        }
    }

    /// <summary>
    /// An output that accepts everything, sounds nothing, and raises PlaybackStopped exactly when
    /// the test says so - which is what makes "did playback wait for the device?" observable.
    /// </summary>
    private sealed class FakeWavePlayer : IWavePlayer
    {
        private readonly bool _stopWhenPlayed;
        private int _playCalls;
        private int _stopCalls;

        public FakeWavePlayer(bool stopWhenPlayed = false)
        {
            _stopWhenPlayed = stopWhenPlayed;
        }

        public event EventHandler<StoppedEventArgs>? PlaybackStopped;

        public float Volume { get; set; } = 1f;

        public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;

        public WaveFormat? OutputWaveFormat { get; private set; }

        public int PlayCalls => Volatile.Read(ref _playCalls);

        public int StopCalls => Volatile.Read(ref _stopCalls);

        public bool Disposed { get; private set; }

        public void Init(IWaveProvider waveProvider) => OutputWaveFormat = waveProvider.WaveFormat;

        public void Play()
        {
            Interlocked.Increment(ref _playCalls);
            PlaybackState = PlaybackState.Playing;

            if (_stopWhenPlayed) RaisePlaybackStopped();
        }

        public void Pause() => PlaybackState = PlaybackState.Paused;

        public void Stop()
        {
            Interlocked.Increment(ref _stopCalls);
            RaisePlaybackStopped();
        }

        public void RaisePlaybackStopped(Exception? exception = null)
        {
            PlaybackState = PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs(exception));
        }

        public void Dispose() => Disposed = true;
    }
}

#pragma warning restore CA1416
