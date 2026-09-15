using System.Reflection;
using EDButtkicker.Configuration;
using EDButtkicker.Models;
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

    // ---- AnnounceAsync ---------------------------------------------------------------------

    /// <summary>
    /// The event pipeline announces on the same path whether or not speech is available, so a
    /// service with no synthesizer has to swallow the announcement rather than fault the task it
    /// handed back - a caller awaiting it is in the middle of dispatching a journal event.
    /// </summary>
    [Fact]
    public async Task AnnounceAsync_WithoutASynthesizer_CompletesRatherThanThrowing()
    {
        using var service = CreateService(new AppSettings(), new FakeAudioOutputFactory());

        await service.AnnounceAsync("Shields offline").WaitAsync(Patience);
        await service.AnnounceAsync("{ship} hull at {health} percent", SampleEvent()).WaitAsync(Patience);
    }

    /// <summary>
    /// The templating is the part of AnnounceAsync a caller can actually get wrong, and it is done
    /// before the string ever reaches the synthesizer - so the message is captured at that seam,
    /// which is the only way to assert on it on a machine with no speech platform.
    /// </summary>
    [Fact]
    public void AnnounceAsync_FillsEveryPlaceholderFromTheJournalEvent()
    {
        using var service = CreateService(new AppSettings(), new FakeAudioOutputFactory());

        var spoken = ProcessMessageTemplate(
            service,
            "{ship} hull at {health} percent, docked at {station} in {system}",
            SampleEvent());

        Assert.Equal("Cobra Mk III hull at 75 percent, docked at Coriolis Station in Sol", spoken);
    }

    /// <summary>
    /// Most journal events carry only a field or two, so the fields that are missing fall back to
    /// generic words: the voice says something a commander can parse, never a raw "{ship}".
    /// </summary>
    [Fact]
    public void AnnounceAsync_WithFieldsMissing_SpeaksGenericWordsNotRawPlaceholders()
    {
        using var service = CreateService(new AppSettings(), new FakeAudioOutputFactory());

        var spoken = ProcessMessageTemplate(
            service,
            "{ship} integrity {health} percent at {station} in {system}",
            new JournalEvent { Event = "HullDamage", Health = 0.5 });

        Assert.Equal("ship integrity 50 percent at station in system", spoken);
        Assert.DoesNotContain('{', spoken);
    }

    /// <summary>
    /// A health of 1.0 is a whole hull, not "100.0" - the percentage is spoken as an integer.
    /// </summary>
    [Fact]
    public void AnnounceAsync_WithNoHealthReported_AssumesAFullHull()
    {
        using var service = CreateService(new AppSettings(), new FakeAudioOutputFactory());

        var spoken = ProcessMessageTemplate(service, "hull {health}", new JournalEvent { Event = "Docked" });

        Assert.Equal("hull 100", spoken);
    }

    /// <summary>With no event to read fields from there is nothing to substitute, so the message is
    /// spoken exactly as the caller wrote it.</summary>
    [Fact]
    public void AnnounceAsync_WithoutAJournalEvent_SpeaksTheMessageUnchanged()
    {
        using var service = CreateService(new AppSettings(), new FakeAudioOutputFactory());

        Assert.Equal("{ship} is fine", ProcessMessageTemplate(service, "{ship} is fine", null));
    }

    // ---- Message source (issue #113) -------------------------------------------------------

    /// <summary>
    /// The canonical voice message for each event lives on the configured HapticPattern, not on a
    /// hardcoded list. GenerateEventMessage must read the configured VoiceMessage and must not
    /// produce a message that differs from what the pattern carries.
    /// </summary>
    [Fact]
    public void GenerateEventMessage_ReturnsMsgFromConfiguredPattern_NotAHardcodedLiteral()
    {
        const string configuredMsg = "Hyperdrive engaged — configured text";
        var fakeSource = new FakePatternSource("FSDJump", new HapticPattern
        {
            EnableVoiceAnnouncement = true,
            VoiceMessage = configuredMsg,
        });

        using var service = CreateService(
            new AppSettings(),
            new FakeAudioOutputFactory(),
            patternSource: () => fakeSource);

        var message = GenerateEventMessage(service, "FSDJump");

        Assert.Equal(configuredMsg, message);
    }

    /// <summary>
    /// When there is no pattern source (old construction path or test without one), the event
    /// generates no message rather than crashing. A blank announcement is dropped before the
    /// synthesizer is ever reached.
    /// </summary>
    [Fact]
    public void GenerateEventMessage_WithNoPatternSource_ReturnsEmpty()
    {
        using var service = CreateService(new AppSettings(), new FakeAudioOutputFactory());

        var message = GenerateEventMessage(service, "FSDJump");

        Assert.Equal(string.Empty, message);
    }

    /// <summary>
    /// A pattern that has voice disabled must not generate a message, even if the VoiceMessage
    /// field is populated — the toggle is what the user uses to silence a noisy event.
    /// </summary>
    [Fact]
    public void GenerateEventMessage_WithVoiceDisabledOnPattern_ReturnsEmpty()
    {
        var fakeSource = new FakePatternSource("FSDJump", new HapticPattern
        {
            EnableVoiceAnnouncement = false,
            VoiceMessage = "should not be spoken",
        });

        using var service = CreateService(
            new AppSettings(),
            new FakeAudioOutputFactory(),
            patternSource: () => fakeSource);

        var message = GenerateEventMessage(service, "FSDJump");

        Assert.Equal(string.Empty, message);
    }

    // ---- Rate limiting ---------------------------------------------------------------------

    /// <summary>
    /// Hull damage arrives in bursts while a fight is going on, and a voice repeating it once per
    /// hit is unusable - the second announcement inside the five-second window is dropped.
    /// </summary>
    [Fact]
    public void ARepeatedEvent_IsDroppedWhileItsWindowIsOpen()
    {
        using var service = CreateService(new AppSettings(), new FakeAudioOutputFactory());
        var lastAnnounced = LastAnnouncementTimes(service);

        // Nothing announced yet, so the first one goes through.
        Assert.False(ShouldRateLimit(service, "HullDamage"));

        lastAnnounced["HullDamage"] = DateTime.UtcNow;

        Assert.True(ShouldRateLimit(service, "HullDamage"));
    }

    /// <summary>
    /// The window is a pause, not a mute: once it has elapsed the next hit is announced again.
    /// Time is moved by backdating the recorded announcement rather than by sleeping.
    /// </summary>
    [Fact]
    public void ARepeatedEvent_IsAnnouncedAgainOnceItsWindowHasElapsed()
    {
        using var service = CreateService(new AppSettings(), new FakeAudioOutputFactory());
        var lastAnnounced = LastAnnouncementTimes(service);

        lastAnnounced["HullDamage"] = DateTime.UtcNow - TimeSpan.FromSeconds(6);

        Assert.False(ShouldRateLimit(service, "HullDamage"));
    }

    /// <summary>Each event type keeps its own window, so a quiet one is not silenced by a noisy
    /// one that was just announced.</summary>
    [Fact]
    public void RateLimiting_IsTrackedPerEventType()
    {
        using var service = CreateService(new AppSettings(), new FakeAudioOutputFactory());
        var lastAnnounced = LastAnnouncementTimes(service);

        lastAnnounced["HullDamage"] = DateTime.UtcNow;

        Assert.True(ShouldRateLimit(service, "HullDamage"));
        Assert.False(ShouldRateLimit(service, "HeatWarning"));
    }

    /// <summary>
    /// Only the events that can repeat are limited. A jump is a one-off the commander asked for, so
    /// it is announced every time however recently the last one was.
    /// </summary>
    [Fact]
    public void AnEventWithNoWindow_IsNeverRateLimited()
    {
        using var service = CreateService(new AppSettings(), new FakeAudioOutputFactory());
        var lastAnnounced = LastAnnouncementTimes(service);

        lastAnnounced["FSDJump"] = DateTime.UtcNow;

        Assert.False(ShouldRateLimit(service, "FSDJump"));
    }

    // ---- ProcessPatternVoiceFeedback -------------------------------------------------------

    /// <summary>
    /// A pattern that only speaks must not open an audio device: the cue and the announcement are
    /// independent switches, and turning one on should not sound the other.
    /// </summary>
    [Fact]
    public async Task APatternThatOnlySpeaks_NeverOpensAnAudioOutput()
    {
        var factory = new FakeAudioOutputFactory();
        using var service = CreateService(new AppSettings(), factory);

        var pattern = new HapticPattern
        {
            EnableVoiceAnnouncement = true,
            VoiceMessage = "Jump in progress",
            EnableAudioCue = false
        };

        await service.ProcessPatternVoiceFeedback(pattern, SampleEvent()).WaitAsync(Patience);

        Assert.Empty(factory.OpenedEndpoints);
        Assert.Equal(0, factory.OpenDefaultCalls);
    }

    /// <summary>The cue plays on its own, without a voice announcement to carry it.</summary>
    [Fact]
    public async Task APatternThatOnlyPlaysACue_ReachesTheDevice()
    {
        using var cue = new TempCueFile();
        var player = new FakeWavePlayer(stopWhenPlayed: true);
        var factory = new FakeAudioOutputFactory(() => player);
        using var service = CreateService(new AppSettings(), factory);

        var pattern = new HapticPattern
        {
            EnableVoiceAnnouncement = false,
            EnableAudioCue = true,
            AudioCueFile = cue.FileName
        };

        await service.ProcessPatternVoiceFeedback(pattern, null).WaitAsync(Patience);

        Assert.Equal(1, player.PlayCalls);
        Assert.Equal(1, factory.OpenDefaultCalls);
    }

    /// <summary>
    /// With both switches on the call waits for the cue as well as the announcement, so the pattern
    /// knows when its feedback has finished sounding.
    /// </summary>
    [Fact]
    public async Task APatternWithBothEnabled_WaitsForTheCueToFinish()
    {
        using var cue = new TempCueFile();
        var player = new FakeWavePlayer();
        var factory = new FakeAudioOutputFactory(() => player);
        using var service = CreateService(new AppSettings(), factory);

        var pattern = new HapticPattern
        {
            EnableVoiceAnnouncement = true,
            VoiceMessage = "Hull at {health} percent",
            EnableAudioCue = true,
            AudioCueFile = cue.FileName
        };

        var feedback = service.ProcessPatternVoiceFeedback(pattern, SampleEvent());

        Assert.Equal(1, player.PlayCalls);
        Assert.False(feedback.IsCompleted, "the pattern should still be waiting on the cue");

        player.RaisePlaybackStopped();
        await feedback.WaitAsync(Patience);
    }

    /// <summary>
    /// Both switches off is the common case - most patterns are haptics only - and it has to cost
    /// nothing: no device opened, and no announcement attempted.
    /// </summary>
    [Fact]
    public async Task APatternWithNeitherEnabled_DoesNothingAtAll()
    {
        var factory = new FakeAudioOutputFactory();
        using var service = CreateService(new AppSettings(), factory);

        var pattern = new HapticPattern
        {
            EnableVoiceAnnouncement = false,
            VoiceMessage = "this should never be spoken",
            EnableAudioCue = false,
            AudioCueFile = "unused.wav"
        };

        await service.ProcessPatternVoiceFeedback(pattern, SampleEvent()).WaitAsync(Patience);

        Assert.Empty(factory.OpenedEndpoints);
        Assert.Equal(0, factory.OpenDefaultCalls);
    }

    /// <summary>
    /// A switch turned on but left unfilled - saved from the pattern editor before a file or a
    /// message was chosen - is treated as off rather than as an empty cue or an empty utterance.
    /// </summary>
    [Fact]
    public async Task APatternWithEmptyMessageAndCue_TreatsBothSwitchesAsOff()
    {
        var factory = new FakeAudioOutputFactory();
        using var service = CreateService(new AppSettings(), factory);

        var pattern = new HapticPattern
        {
            EnableVoiceAnnouncement = true,
            VoiceMessage = string.Empty,
            EnableAudioCue = true,
            AudioCueFile = string.Empty
        };

        await service.ProcessPatternVoiceFeedback(pattern, SampleEvent()).WaitAsync(Patience);

        Assert.Empty(factory.OpenedEndpoints);
        Assert.Equal(0, factory.OpenDefaultCalls);
    }

    /// <summary>A cue file that has gone missing must not fault the pattern that asked for it.</summary>
    [Fact]
    public async Task APatternWhoseCueFileIsGone_StillCompletes()
    {
        var factory = new FakeAudioOutputFactory();
        using var service = CreateService(new AppSettings(), factory);

        var pattern = new HapticPattern
        {
            EnableVoiceAnnouncement = true,
            VoiceMessage = "Hull breach",
            EnableAudioCue = true,
            AudioCueFile = $"not-a-file-{Guid.NewGuid():N}.wav"
        };

        await service.ProcessPatternVoiceFeedback(pattern, SampleEvent()).WaitAsync(Patience);

        Assert.Empty(factory.OpenedEndpoints);
        Assert.Equal(0, factory.OpenDefaultCalls);
    }

    private static VoiceFeedbackService CreateService(
        AppSettings settings,
        IAudioOutputFactory factory,
        IAudioDeviceCatalog? catalog = null,
        Func<IEventPatternSource?>? patternSource = null) =>
        new(NullLogger<VoiceFeedbackService>.Instance, settings, catalog, factory, patternSource);

    private static string GenerateEventMessage(VoiceFeedbackService service, string eventType, JournalEvent? journalEvent = null) =>
        (string)typeof(VoiceFeedbackService)
            .GetMethod("GenerateEventMessage", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, new object?[] { eventType, journalEvent })!;

    private static JournalEvent SampleEvent() => new()
    {
        Event = "HullDamage",
        Ship = "Cobra Mk III",
        Health = 0.75,
        StationName = "Coriolis Station",
        StarSystem = "Sol"
    };

    // System.Speech is Windows-only, so the message can only be observed where it is written rather
    // than where it is spoken, and the window itself is consulted before anything is announced.
    // Both are internal to the service; reaching them by reflection is what lets these run on a
    // machine with no speech platform.

    private static string ProcessMessageTemplate(
        VoiceFeedbackService service,
        string template,
        JournalEvent? journalEvent) =>
        (string)typeof(VoiceFeedbackService)
            .GetMethod("ProcessMessageTemplate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, new object?[] { template, journalEvent })!;

    private static bool ShouldRateLimit(VoiceFeedbackService service, string eventType) =>
        (bool)typeof(VoiceFeedbackService)
            .GetMethod("ShouldRateLimit", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, new object?[] { eventType })!;

    private static Dictionary<string, DateTime> LastAnnouncementTimes(VoiceFeedbackService service) =>
        (Dictionary<string, DateTime>)typeof(VoiceFeedbackService)
            .GetField("_lastAnnouncementTimes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service)!;

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

    private sealed class FakePatternSource : IEventPatternSource
    {
        private readonly string _eventType;
        private readonly HapticPattern _pattern;

        public FakePatternSource(string eventType, HapticPattern pattern)
        {
            _eventType = eventType;
            _pattern = pattern;
        }

        public HapticPattern? GetPattern(string eventType) =>
            eventType == _eventType ? _pattern : null;
    }
}

#pragma warning restore CA1416
