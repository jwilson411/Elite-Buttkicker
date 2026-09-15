using Microsoft.Extensions.Logging;
using System.Speech.Synthesis;
using NAudio.Wave;
using EDButtkicker.Models;
using EDButtkicker.Configuration;
using System.Runtime.Versioning;

namespace EDButtkicker.Services;

[SupportedOSPlatform("windows")]
public class VoiceFeedbackService : IVoiceFeedback, IDisposable
{
    /// <summary>The range System.Speech accepts for <see cref="SpeechSynthesizer.Volume"/>.</summary>
    private const int MinVolume = 0;
    private const int MaxVolume = 100;

    /// <summary>The range System.Speech accepts for <see cref="SpeechSynthesizer.Rate"/>.</summary>
    private const int MinRate = -10;
    private const int MaxRate = 10;

    private readonly ILogger<VoiceFeedbackService> _logger;
    private readonly AppSettings _settings;
    private readonly IAudioDeviceCatalog? _deviceCatalog;
    private readonly IAudioOutputFactory? _outputFactory;
    private readonly SpeechSynthesizer? _synthesizer;
    private readonly Func<IEventPatternSource?>? _patternSource;
    private readonly Dictionary<string, DateTime> _lastAnnouncementTimes = new();

    /// <summary>Cancels any cue still playing when the service goes away.</summary>
    private readonly CancellationTokenSource _disposeCts = new();

    private bool _isInitialized = false;
    private volatile bool _disposed;

    /// <summary>
    /// The catalog and the output factory are the same seams <see cref="AudioEngineService"/> uses,
    /// so a cue plays out of the device the user selected rather than out of whatever Windows calls
    /// the default. Both are optional: with neither one, cue playback falls back to the default
    /// output, which is what the service did before it could route.
    ///
    /// The pattern source is reached through a delegate rather than injected directly because the
    /// service that holds the mappings depends on this one - resolving it here would close a
    /// construction cycle. Nothing is looked up until an event is announced, by which time the
    /// mapping service exists. Null where there is nothing to read mappings from, which leaves an
    /// event with no message to speak rather than a failure.
    /// </summary>
    public VoiceFeedbackService(
        ILogger<VoiceFeedbackService> logger,
        AppSettings settings,
        IAudioDeviceCatalog? deviceCatalog = null,
        IAudioOutputFactory? outputFactory = null,
        Func<IEventPatternSource?>? patternSource = null)
    {
        _logger = logger;
        _settings = settings;
        _deviceCatalog = deviceCatalog;
        _outputFactory = outputFactory;
        _patternSource = patternSource;

        // System.Speech exists on Windows and nowhere else, and it can also fail on a Windows box
        // with no speech platform installed. A service that cannot speak still plays audio cues, so
        // the failure is recorded and IsRunning stays false rather than taking construction down.
        try
        {
            _synthesizer = new SpeechSynthesizer();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Speech synthesis is unavailable; voice announcements are disabled");
        }
    }

    /// <summary>
    /// True once <see cref="Initialize"/> has started a synthesizer. Until then every announcement
    /// is dropped, so the health dashboard reports this rather than assuming speech works.
    /// </summary>
    public bool IsRunning => _isInitialized;

    public void Initialize()
    {
        if (_synthesizer == null)
        {
            _logger.LogWarning("Voice Feedback Service has no synthesizer; announcements stay disabled");
            return;
        }

        try
        {
            _logger.LogInformation("Initializing Voice Feedback Service");

            // Volume and rate are the user's, read from settings rather than fixed here, so the
            // values the settings panel shows are the ones the voice actually speaks with.
            ApplySettingsToSynthesizer();


            // Try to set a suitable voice
            var voices = _synthesizer.GetInstalledVoices();
            var preferredVoice = voices.FirstOrDefault(v => 
                v.VoiceInfo.Name.Contains("Microsoft") && 
                v.VoiceInfo.Culture.Name.StartsWith("en"));
            
            if (preferredVoice != null)
            {
                _synthesizer.SelectVoice(preferredVoice.VoiceInfo.Name);
                _logger.LogInformation("Selected voice: {VoiceName}", preferredVoice.VoiceInfo.Name);
            }
            else
            {
                _logger.LogInformation("Using default voice: {VoiceName}", _synthesizer.Voice.Name);
            }

            _isInitialized = true;
            _logger.LogInformation("Voice Feedback Service initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize voice feedback service");
        }
    }

    /// <summary>
    /// Takes a volume or rate change live. The settings object is the record of the choice, so it is
    /// updated here too and a synthesizer started later reads the same values.
    /// </summary>
    public bool ApplyVoiceSettings(int volume, int rate)
    {
        _settings.Voice.Volume = volume;
        _settings.Voice.Rate = rate;

        if (_synthesizer == null || !_isInitialized) return false;

        try
        {
            ApplySettingsToSynthesizer();
            return true;
        }
        catch (Exception ex)
        {
            // A voice that keeps its old loudness is worth a log line, not a failed settings save.
            _logger.LogError(ex, "Could not apply voice volume {Volume} and rate {Rate}", volume, rate);
            return false;
        }
    }

    /// <summary>
    /// Values outside what System.Speech accepts would throw rather than be ignored, and a settings
    /// file edited by hand is exactly where one comes from, so they are clamped on the way in.
    /// </summary>
    private void ApplySettingsToSynthesizer()
    {
        if (_synthesizer == null) return;

        _synthesizer.Volume = Math.Clamp(_settings.Voice.Volume, MinVolume, MaxVolume);
        _synthesizer.Rate = Math.Clamp(_settings.Voice.Rate, MinRate, MaxRate);

        _logger.LogDebug("Voice volume {Volume}, rate {Rate}", _synthesizer.Volume, _synthesizer.Rate);
    }

    /// <summary>
    /// Speaks the line configured for <paramref name="eventType"/>, subject to the announcement's
    /// own rate limit. An event with nothing configured to say is silent.
    /// </summary>
    public async Task AnnounceEvent(string eventType, JournalEvent? journalEvent = null)
    {
        if (!_isInitialized || _synthesizer == null) return;

        try
        {
            // Rate limiting for announcements
            if (ShouldRateLimit(eventType))
            {
                _logger.LogDebug("Rate limiting voice announcement for: {EventType}", eventType);
                return;
            }

            string message = GenerateEventMessage(eventType, journalEvent);
            if (string.IsNullOrEmpty(message)) return;

            _logger.LogDebug("Voice announcement: {Message}", message);
            
            // Announce asynchronously to avoid blocking
            await Task.Run(() =>
            {
                try
                {
                    _synthesizer.Speak(message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during speech synthesis");
                }
            });

            _lastAnnouncementTimes[eventType] = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error announcing event: {EventType}", eventType);
        }
    }

    /// <summary>
    /// Plays one cue file through the configured output. Completion is the device's own
    /// PlaybackStopped callback rather than a polling loop, so a cue costs no thread while it
    /// sounds, and disposing the service stops whatever is still playing.
    /// </summary>
    public async Task PlayAudioCue(string cueFile)
    {
        if (string.IsNullOrEmpty(cueFile) || _disposed) return;

        string cuePath = Path.Combine("audio_cues", cueFile);
        if (!File.Exists(cuePath))
        {
            _logger.LogWarning("Audio cue file not found: {CueFile}", cuePath);
            return;
        }

        AudioFileReader? audioFile = null;
        IWavePlayer? player = null;

        try
        {
            var cancellation = _disposeCts.Token;
            var completed = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

            audioFile = new AudioFileReader(cuePath);
            var output = OpenCueOutput();
            player = output;
            output.PlaybackStopped += (_, args) =>
            {
                if (args.Exception != null)
                {
                    completed.TrySetException(args.Exception);
                }
                else
                {
                    completed.TrySetResult(null);
                }
            };

            // Stopping the device raises PlaybackStopped, but a device that never got as far as
            // playing will not, so the task is completed here as well and the first result wins.
            using var cancelRegistration = cancellation.Register(() =>
            {
                try
                {
                    output.Stop();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error stopping cancelled audio cue: {CueFile}", cueFile);
                }

                completed.TrySetCanceled(cancellation);
            });

            output.Init(audioFile);
            output.Play();

            await completed.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Audio cue playback cancelled: {CueFile}", cueFile);
        }
        catch (ObjectDisposedException)
        {
            // The service was disposed between the guard above and the token being read.
            _logger.LogDebug("Audio cue playback abandoned, service disposed: {CueFile}", cueFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error playing audio cue: {CueFile}", cueFile);
        }
        finally
        {
            player?.Dispose();
            audioFile?.Dispose();
        }
    }

    /// <summary>
    /// Opens the output a cue should sound on: the saved endpoint when it resolves against the live
    /// enumeration, and the default output otherwise. This is the resolution
    /// <see cref="AudioEngineService"/> uses for haptic patterns, so a cue and a pattern land on the
    /// same device. Without an output factory there is nothing to route through and a cue plays on
    /// the default WinMM output, exactly as it did before.
    /// </summary>
    private IWavePlayer OpenCueOutput()
    {
        if (_outputFactory == null) return new WaveOutEvent();

        var resolution = AudioDeviceResolver.Resolve(
            TryEnumerateDevices(),
            _settings.Audio.AudioDeviceEndpointId,
            _settings.Audio.AudioDeviceName,
            _settings.Audio.AudioDeviceId);

        if (resolution.IsUsable)
        {
            try
            {
                return _outputFactory.OpenEndpoint(resolution.EndpointId).Player;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audio cue endpoint {EndpointId} could not be opened, falling back to default",
                    resolution.EndpointId);
            }
        }
        else if (!resolution.IsSystemDefault)
        {
            _logger.LogWarning("Audio cue output - {Selection}", resolution.Reason);
        }

        return _outputFactory.OpenDefault().Player;
    }

    /// <summary>An enumeration that fails leaves the saved selection unresolvable, which the
    /// resolver already reads as "use the system default" - so a cue still plays.</summary>
    private IReadOnlyList<AudioDevice>? TryEnumerateDevices()
    {
        if (_deviceCatalog == null) return null;

        try
        {
            return _deviceCatalog.GetDevices();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate render endpoints for audio cue playback");
            return null;
        }
    }

    public async Task ProcessPatternVoiceFeedback(HapticPattern pattern, JournalEvent? journalEvent = null)
    {
        var tasks = new List<Task>();

        // Voice announcement
        if (pattern.EnableVoiceAnnouncement && !string.IsNullOrEmpty(pattern.VoiceMessage))
        {
            string message = ProcessMessageTemplate(pattern.VoiceMessage, journalEvent);
            tasks.Add(AnnounceCustomMessage(message));
        }

        // Audio cue
        if (pattern.EnableAudioCue && !string.IsNullOrEmpty(pattern.AudioCueFile))
        {
            tasks.Add(PlayAudioCue(pattern.AudioCueFile));
        }

        if (tasks.Any())
        {
            await Task.WhenAll(tasks);
        }
    }

    /// <summary>
    /// The one entry point the event pipeline uses. Templating happens here so a caller only has to
    /// pick which message to speak, not know how a message is written.
    /// </summary>
    public Task AnnounceAsync(string message, JournalEvent? journalEvent = null) =>
        AnnounceCustomMessage(ProcessMessageTemplate(message, journalEvent));

    private async Task AnnounceCustomMessage(string message)
    {
        if (!_isInitialized || _synthesizer == null || string.IsNullOrEmpty(message)) return;

        try
        {
            await Task.Run(() =>
            {
                try
                {
                    _synthesizer.Speak(message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during custom message synthesis");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error announcing custom message");
        }
    }

    /// <summary>
    /// What to say for one event, taken from the pattern mapped to it - the same
    /// <see cref="HapticPattern.VoiceMessage"/> that <see cref="ProcessPatternVoiceFeedback"/>
    /// speaks, templated the same way. An event with no mapping, with voice switched off, or with
    /// no message written has nothing to say and returns empty, which callers drop rather than
    /// speak; the alternative - a message kept here - is a second place to edit and a place for the
    /// voice to disagree with the pattern the user configured.
    /// </summary>
    private string GenerateEventMessage(string eventType, JournalEvent? journalEvent)
    {
        var pattern = TryGetEventPattern(eventType);

        if (pattern is not { EnableVoiceAnnouncement: true } || string.IsNullOrWhiteSpace(pattern.VoiceMessage))
        {
            return string.Empty;
        }

        return ProcessMessageTemplate(pattern.VoiceMessage, journalEvent);
    }

    /// <summary>A mapping lookup that fails leaves the event unannounced - it must not fault the
    /// haptics the announcement came with.</summary>
    private HapticPattern? TryGetEventPattern(string eventType)
    {
        try
        {
            return _patternSource?.Invoke()?.GetPattern(eventType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the configured pattern for {EventType}", eventType);
            return null;
        }
    }

    private string ProcessMessageTemplate(string template, JournalEvent? journalEvent)
    {
        if (journalEvent == null) return template;

        string processed = template;
        processed = processed.Replace("{ship}", journalEvent.Ship ?? "ship");
        processed = processed.Replace("{health}", ((int)((journalEvent.Health ?? 1.0) * 100)).ToString());
        processed = processed.Replace("{station}", journalEvent.StationName ?? "station");
        processed = processed.Replace("{system}", journalEvent.StarSystem ?? "system");
        
        return processed;
    }

    private bool ShouldRateLimit(string eventType)
    {
        var rateLimits = new Dictionary<string, TimeSpan>
        {
            ["HullDamage"] = TimeSpan.FromSeconds(5),
            ["HeatWarning"] = TimeSpan.FromSeconds(10),
            ["HeatDamage"] = TimeSpan.FromSeconds(3),
            ["UnderAttack"] = TimeSpan.FromSeconds(2),
            ["ShieldDown"] = TimeSpan.FromSeconds(5),
            ["ShieldsUp"] = TimeSpan.FromSeconds(5)
        };

        if (!rateLimits.TryGetValue(eventType, out var minInterval))
            return false;

        if (!_lastAnnouncementTimes.TryGetValue(eventType, out var lastTime))
            return false;

        return DateTime.UtcNow - lastTime < minInterval;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            // Cancel first: a cue that is still sounding has to be stopped before the objects it
            // plays through are torn down.
            _disposeCts.Cancel();
            _disposeCts.Dispose();
            _synthesizer?.Dispose();
            _logger.LogInformation("Voice Feedback Service disposed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing Voice Feedback Service");
        }
    }
}