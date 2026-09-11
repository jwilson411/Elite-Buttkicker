using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;
using NAudio.Wasapi;
using EDButtkicker.Configuration;
using EDButtkicker.Models;

namespace EDButtkicker.Services;

public class AudioEngineService : IDisposable
{
    private readonly ILogger<AudioEngineService> _logger;
    private readonly AppSettings _settings;
    // Enumeration and opening are both behind seams: nothing here constructs a WASAPI or WinMM
    // object, so the fallback rules are exercisable on a machine with no output device at all.
    private readonly IAudioDeviceCatalog _deviceCatalog;
    private readonly IAudioOutputFactory _outputFactory;
    private IWavePlayer? _waveOut;
    private MixingSampleProvider? _mixer;
    private readonly object _lock = new object();
    private bool _isInitialized = false;
    private bool _initializationFailed = false;
    private string? _lastInitializationError;
    private DateTime? _openedAtUtc;
    // What was actually opened, as opposed to what the settings asked for: the fallback path can
    // land on a different backend and a different endpoint than the saved selection.
    private string? _backend;
    private string? _activeEndpointId;
    private string? _activeDeviceName;
    private string? _lastPlaybackError;
    private DateTime? _lastPlaybackAtUtc;
    // One registry, not two parallel maps: the mixer input, its cancellation and its scheduled
    // cleanup belong to the same effect, so they are added and removed as a single entry and cannot
    // drift apart.
    private readonly Dictionary<string, ActiveEffect> _activeEffects = new();

    /// <summary>
    /// The catalog and the output factory are optional so the tests that only need playback
    /// bookkeeping keep constructing this with a logger and settings; the container always supplies
    /// the registered implementations.
    /// </summary>
    public AudioEngineService(
        ILogger<AudioEngineService> logger,
        AppSettings settings,
        IAudioDeviceCatalog? deviceCatalog = null,
        IAudioOutputFactory? outputFactory = null)
    {
        _logger = logger;
        _settings = settings;
        _deviceCatalog = deviceCatalog ??
            new WasapiAudioDeviceCatalog(NullLogger<WasapiAudioDeviceCatalog>.Instance);
        _outputFactory = outputFactory ??
            new NAudioOutputFactory(NullLogger<NAudioOutputFactory>.Instance);
    }

    public void Initialize()
    {
        lock (_lock)
        {
            if (_isInitialized)
            {
                _logger.LogDebug("Audio Engine already initialized, skipping");
                return;
            }

            try
            {
                _logger.LogInformation("Initializing Audio Engine");
                LogSystemAudioInfo();
                _waveOut = OpenConfiguredOutput();

                // Log wave output configuration
                LogWaveOutConfiguration();

                // Create mixer for combining multiple audio streams
                var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_settings.Audio.SampleRate, 1);
                _mixer = new MixingSampleProvider(waveFormat);
                _mixer.ReadFully = true; // Ensure smooth playback

                _logger.LogDebug("Created mixer with format: {SampleRate}Hz, {Channels} channel(s), {BitsPerSample}-bit float", 
                    waveFormat.SampleRate, waveFormat.Channels, waveFormat.BitsPerSample);

                // Start the output
                _logger.LogDebug("Initializing wave output with mixer...");
                _waveOut.Init(_mixer);
                
                _logger.LogDebug("Starting wave output playback...");
                _waveOut.Play();
                
                // Verify playback state
                var playbackState = _waveOut.PlaybackState;
                _logger.LogDebug("Wave output playback state: {PlaybackState}", playbackState);

                _isInitialized = true;
                _lastInitializationError = null;
                _openedAtUtc = DateTime.UtcNow;
                _logger.LogInformation("✓ Audio Engine initialized successfully");
                _logger.LogInformation("Configuration: Sample Rate: {SampleRate}Hz, Buffer Size: {BufferSize}, Channels: 1", 
                    _settings.Audio.SampleRate, _settings.Audio.BufferSize);
                _logger.LogInformation("WaveOut PlaybackState: {PlaybackState}", _waveOut.PlaybackState);
            }
            catch (Exception ex)
            {
                // Kept so the health API can name the actual failure rather than "offline".
                _lastInitializationError = ex.Message;
                _logger.LogError(ex, "❌ Failed to initialize audio engine: {ErrorMessage}", ex.Message);
                LogDetailedAudioError(ex);
                throw;
            }
        }
    }

    /// <summary>
    /// Opens the audio device on first use. Playback is the trigger, so nothing that merely
    /// constructs or resolves this service touches audio hardware. Unlike <see cref="Initialize"/>
    /// this never throws: a machine with no usable output device just gets no haptics, and one
    /// failed attempt is remembered so every later pattern does not retry the device enumeration.
    /// </summary>
    public virtual bool EnsureInitialized()
    {
        lock (_lock)
        {
            if (_isInitialized) return true;
            if (_initializationFailed) return false;

            try
            {
                Initialize();
            }
            catch (Exception ex)
            {
                _initializationFailed = true;
                _lastInitializationError = ex.Message;
                _logger.LogError(ex, "Audio engine unavailable, haptics are disabled for this session");
                return false;
            }

            return _isInitialized;
        }
    }

    /// <summary>
    /// What the audio output is actually doing. Reading this never opens a device, so the health
    /// API can report "not opened yet" honestly instead of guessing from an unrelated API call.
    /// </summary>
    public virtual AudioEngineStatus GetStatus()
    {
        lock (_lock)
        {
            return new AudioEngineStatus(
                _isInitialized,
                _initializationFailed,
                _lastInitializationError,
                _settings.Audio.AudioDeviceName,
                _openedAtUtc,
                _backend,
                _activeDeviceName,
                _activeEndpointId,
                _lastPlaybackError,
                _lastPlaybackAtUtc,
                _activeEffects.Count);
        }
    }

    /// <summary>
    /// Forgets a previous failure and tries to open the device again. This is what the health
    /// indicator's retry runs; it reports the real outcome rather than clearing the warning.
    /// </summary>
    public bool RetryInitialization()
    {
        lock (_lock)
        {
            _initializationFailed = false;
            _lastInitializationError = null;
        }

        return EnsureInitialized();
    }

    /// <summary>
    /// Plays one pattern and reports whether it actually reached an open output. Virtual so tests
    /// can observe what would be played without opening a device. Nothing throws: a caller that
    /// only wants haptics-if-available can ignore the result, while the test endpoints turn a
    /// failure into an HTTP failure rather than reporting a success that never made a sound.
    /// </summary>
    public virtual Task<AudioPlaybackResult> TryPlayHapticPattern(HapticPattern pattern, JournalEvent? journalEvent = null)
    {
        if (!EnsureInitialized())
        {
            _logger.LogWarning("⚠ Audio engine not initialized, skipping playback for pattern: {PatternName}", pattern.Name);

            var reason = string.IsNullOrWhiteSpace(_lastInitializationError)
                ? "No audio output device could be opened."
                : $"No audio output device could be opened: {_lastInitializationError}";

            return Task.FromResult(RecordPlaybackFailure(reason));
        }

        // Read once, under the lock: a concurrent Reinitialize replaces this field, and re-reading
        // it below would turn that race into a NullReferenceException out of a call that is
        // documented never to throw.
        IWavePlayer? output;
        lock (_lock)
        {
            output = _waveOut;
        }

        // Check if wave output is still valid
        if (output == null)
        {
            _logger.LogError("❌ Wave output is null, cannot play pattern: {PatternName}", pattern.Name);
            return Task.FromResult(RecordPlaybackFailure("The audio output was closed, so nothing could be played."));
        }

        var playbackState = output.PlaybackState;
        if (playbackState != PlaybackState.Playing)
        {
            _logger.LogWarning("⚠ Wave output not in playing state ({PlaybackState}), attempting to restart for pattern: {PatternName}",
                playbackState, pattern.Name);
            try
            {
                output.Play();
                _logger.LogDebug("✓ Wave output restarted successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to restart wave output");
                return Task.FromResult(RecordPlaybackFailure($"The audio output could not be restarted: {ex.Message}"));
            }
        }

        try
        {
            var effectId = Guid.NewGuid().ToString();
            _logger.LogDebug("🎵 Playing haptic pattern: '{PatternName}' (ID: {EffectId})", pattern.Name, effectId);

            // Calculate intensity
            var intensity = CalculateIntensity(pattern, journalEvent);
            var frequency = pattern.Frequency;

            _logger.LogDebug("Pattern configuration - Frequency: {Frequency}Hz, Intensity: {Intensity}%, Duration: {Duration}ms, Type: {PatternType}",
                frequency, intensity, pattern.Duration, pattern.Pattern);

            // Log current mixer state
            lock (_lock)
            {
                _logger.LogDebug("Current active effects: {ActiveCount}, Mixer inputs: {MixerInputs}", 
                    _activeEffects.Count, _mixer?.MixerInputs?.Count() ?? 0);
            }

            // Create appropriate sample provider based on pattern type.
            // The factory applies the app-level hardware safety limiter as the final stage for
            // every pattern type, so nothing reaches the mixer above Audio.MaxIntensity.
            ISampleProvider sampleProvider = HapticSampleFactory.Create(
                pattern, intensity, frequency, _settings.Audio.SampleRate, _settings.Audio.MaxIntensity);

            _logger.LogDebug("Created sample provider type: {SampleProviderType}, limited to {MaxIntensity}% max intensity",
                sampleProvider.GetType().Name, _settings.Audio.MaxIntensity);

            // Everything that reaches the mixer goes through the cancellation envelope, so a stop
            // ramps this effect to zero instead of removing it at whatever amplitude it is at.
            // The envelope only attenuates, so the safety limiter inside the factory still holds.
            var mixerInput = new ReleaseEnvelopeSampleProvider(sampleProvider, HapticEnvelope.ReleaseMilliseconds);

            // The cancellation is part of the registry entry, so a concurrent stop that sees the
            // effect always sees its cancellation too and cannot leave the scheduled cleanup
            // running against an input it has already removed.
            var effect = new ActiveEffect(mixerInput, new CancellationTokenSource());

            // Taken before the effect is published: once another thread can stop it, the source can
            // be disposed, and reading Token from a disposed source throws.
            var cleanupToken = effect.Cancellation.Token;

            lock (_lock)
            {
                try
                {
                    _mixer?.AddMixerInput(mixerInput);
                }
                catch (Exception ex)
                {
                    // Nothing was registered yet, so the failed effect leaves no entry behind - the
                    // registry never names an input the mixer is not reading.
                    _logger.LogError(ex, "❌ Failed to add sample provider to mixer");
                    effect.Discard();
                    throw;
                }

                _activeEffects[effectId] = effect;
                _logger.LogDebug("✓ Added sample provider to mixer successfully. Active effects: {Count}", _activeEffects.Count);
            }

            var cleanupDelay = pattern.Duration + pattern.FadeOut + 100;
            _logger.LogDebug("Scheduling cleanup for effect {EffectId} in {CleanupDelay}ms", effectId, cleanupDelay);

            // Schedule cleanup after pattern duration. The handle is kept on the entry rather than
            // discarded, so the scheduled work for an effect is observable from the registry.
            var cleanupTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(cleanupDelay, cleanupToken);
                    CleanupEffect(effectId);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Effect cleanup cancelled for {EffectId} (manual stop)", effectId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during scheduled cleanup for effect {EffectId}", effectId);
                }
            });

            lock (_lock)
            {
                // The effect may already have been stopped and removed by now; recording the handle
                // on an entry nobody holds is harmless, and the live entry gets it either way.
                effect.Cleanup = cleanupTask;
            }

            _logger.LogDebug("✓ Successfully initiated playback for pattern '{PatternName}' with effect ID: {EffectId}", pattern.Name, effectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error playing haptic pattern '{PatternName}': {ErrorMessage}", pattern.Name, ex.Message);
            LogDetailedAudioError(ex);

            return Task.FromResult(RecordPlaybackFailure($"Playback failed: {ex.Message}"));
        }

        lock (_lock)
        {
            _lastPlaybackError = null;
            _lastPlaybackAtUtc = DateTime.UtcNow;
        }

        return Task.FromResult(AudioPlaybackResult.Success);
    }

    /// <summary>
    /// Plays one pattern for callers that want haptics if a device is available and nothing at all
    /// otherwise - the journal-driven path, where a missing device must not break event handling.
    /// </summary>
    public virtual Task PlayHapticPattern(HapticPattern pattern, JournalEvent? journalEvent = null) =>
        TryPlayHapticPattern(pattern, journalEvent);

    /// <summary>Remembers why the last playback did not reach the output, for the health payload.</summary>
    private AudioPlaybackResult RecordPlaybackFailure(string error)
    {
        lock (_lock)
        {
            _lastPlaybackError = error;
        }

        return AudioPlaybackResult.Failed(error);
    }

    private int CalculateIntensity(HapticPattern pattern, JournalEvent? journalEvent)
    {
        if (!pattern.IntensityFromDamage || journalEvent?.HullDamage == null)
        {
            return Math.Min(pattern.Intensity, _settings.Audio.MaxIntensity);
        }

        // Scale intensity based on hull damage (0-1 scale)
        var damagePercent = journalEvent.HullDamage.Value;
        var scaledIntensity = (int)(pattern.MinIntensity + (damagePercent * (pattern.MaxIntensity - pattern.MinIntensity)));
        
        return Math.Min(scaledIntensity, _settings.Audio.MaxIntensity);
    }

    /// <summary>
    /// Tears one effect down. Taking the entry out of the registry is the only way in, so cleaning
    /// the same id up twice - a scheduled cleanup arriving after a manual stop already removed the
    /// effect - is a no-op rather than a second cancel and dispose of the same token source.
    /// </summary>
    private void CleanupEffect(string effectId)
    {
        lock (_lock)
        {
            if (!_activeEffects.Remove(effectId, out var effect))
            {
                return;
            }

            try
            {
                _mixer?.RemoveMixerInput(effect.MixerInput);
                _logger.LogDebug("Cleaned up audio effect: {EffectId}", effectId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cleaning up audio effect: {EffectId}", effectId);
            }

            effect.Discard();
        }
    }

    /// <summary>
    /// Silences everything that is playing right now and returns how many effects were stopped.
    /// This is the panic button behind the UI's stop control, so it takes effect immediately rather
    /// than waiting for the scheduled cleanup of each effect. "Immediately" still means a ramp:
    /// every active input is released to zero gain over
    /// <see cref="HapticEnvelope.ReleaseMilliseconds"/> and only then removed from the mixer, so a
    /// stop is a fade rather than a full-amplitude cut into the transducer.
    /// </summary>
    public virtual int StopAllEffects()
    {
        var stopped = BeginReleaseOfAllEffects();

        // Give the output the bounded interval it needs to actually play the ramp out. The wait is
        // deliberately outside the lock: holding it here would block every status read and every
        // new pattern for the length of the ramp. Nothing was playing means nothing to ramp, so a
        // stop with no active effects still returns at once.
        if (stopped > 0)
        {
            Thread.Sleep(HapticEnvelope.ReleaseMilliseconds + HapticEnvelope.ReleaseSettleMilliseconds);
        }

        RemoveAllReleasedEffects();
        return stopped;
    }

    /// <summary>
    /// Cancels the scheduled cleanups and starts every active effect's ramp to zero, returning how
    /// many were released. Takes <see cref="_lock"/> and does not wait, so the caller owns the ramp
    /// interval and no lock is held across it.
    /// </summary>
    private int BeginReleaseOfAllEffects()
    {
        lock (_lock)
        {
            var stopped = _activeEffects.Count;
            _logger.LogInformation("Stopping all active audio effects ({Count})", stopped);

            foreach (var effect in _activeEffects.Values)
            {
                effect.CancelCleanup();
                effect.MixerInput.BeginRelease();
            }

            return stopped;
        }
    }

    /// <summary>
    /// Drops everything the mixer is reading and forgets the bookkeeping. Called once the release
    /// ramp has had its interval, so the inputs being removed are already silent. Emptying an
    /// already empty registry is a no-op, so this is safe to call from the stop, reinitialize and
    /// dispose paths in any order.
    /// </summary>
    private void RemoveAllReleasedEffects()
    {
        lock (_lock)
        {
            foreach (var effect in _activeEffects.Values)
            {
                _mixer?.RemoveMixerInput(effect.MixerInput);
                effect.Discard();
            }

            _activeEffects.Clear();

            // Clear mixer
            _mixer?.RemoveAllMixerInputs();
        }
    }

    public void Reinitialize()
    {
        _logger.LogInformation("Reinitializing Audio Engine with new device settings");

        // The ramp-out has to happen before the lock is taken. StopAllEffects acquires _lock
        // itself and waits out the release interval, so calling it from inside the lock below
        // would hold _lock across that wait and stall every other caller of this service.
        StopAllEffects();

        lock (_lock)
        {
            if (_waveOut != null)
            {
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = null;
            }
            
            _mixer = null;
            _isInitialized = false;
            _openedAtUtc = null;
            _backend = null;
            _activeEndpointId = null;
            _activeDeviceName = null;
            _lastPlaybackError = null;
            _lastPlaybackAtUtc = null;
            // A new device deserves a fresh attempt even if the previous one could not be opened.
            _initializationFailed = false;
            _lastInitializationError = null;
            
            // Anything the stop above raced with - an effect started while it was ramping out - is
            // cancelled and forgotten here, against the mixer that is about to be replaced.
            RemoveAllReleasedEffects();

            // Initialize with new settings
            Initialize();
        }
    }

    private void LogSystemAudioInfo()
    {
        try
        {
            _logger.LogDebug("=== System Audio Information ===");

            // The catalog already reports whatever enumeration is possible here, so a machine with
            // no WASAPI at all logs the system default entry rather than an exception.
            var devices = _deviceCatalog.GetDevices();

            _logger.LogDebug("Render devices reported: {Count}", devices.Count);
            foreach (var device in devices)
            {
                _logger.LogDebug("Device {Index}: '{FriendlyName}' - ID: {DeviceId}, Driver: {Driver}, Active: {IsAvailable}",
                    device.DeviceId, device.Name, device.EndpointId, device.Driver, device.IsAvailable);
            }

            var defaultDevice = devices.FirstOrDefault(d => d.IsDefault);
            if (defaultDevice != null)
            {
                _logger.LogDebug("Default render device: '{FriendlyName}' - ID: {DeviceId}",
                    defaultDevice.Name, defaultDevice.EndpointId);
            }

            _logger.LogDebug("=== End System Audio Information ===");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging system audio information");
        }
    }

    /// <summary>
    /// Opens the output the saved settings actually name. The saved MMDevice endpoint id is the
    /// identity, so a device that has moved in the enumeration still opens, and a device that has
    /// gone away falls back to the system default output rather than to whatever now occupies its
    /// old index. Every WASAPI render endpoint and the resolution itself are logged first, so a
    /// mis-selection report can be read against the endpoint ids.
    /// </summary>
    private IWavePlayer OpenConfiguredOutput()
    {
        IReadOnlyList<AudioDevice> enumerated;

        try
        {
            enumerated = _deviceCatalog.GetDevices();
        }
        catch (Exception ex)
        {
            // No enumeration at all here: the default output is the only thing left to try.
            _logger.LogWarning(ex, "Failed to enumerate render endpoints, using the default output");
            return OpenDefaultOutput();
        }

        foreach (var line in AudioDeviceDiagnostics.DescribeEnumeration(enumerated, "WASAPI render"))
        {
            _logger.LogInformation("Audio devices - {Device}", line);
        }

        var resolution = AudioDeviceResolver.Resolve(
            enumerated,
            _settings.Audio.AudioDeviceEndpointId,
            _settings.Audio.AudioDeviceName,
            _settings.Audio.AudioDeviceId);

        _logger.LogInformation("Audio devices - {Selection}", resolution.Reason);

        if (resolution.IsUsable)
        {
            try
            {
                var handle = _outputFactory.OpenEndpoint(resolution.EndpointId);
                _logger.LogInformation("✓ Using audio device: {DeviceName} (endpoint {EndpointId})",
                    handle.DeviceName ?? resolution.Name, resolution.EndpointId);

                _backend = handle.Backend;
                _activeEndpointId = handle.EndpointId ?? resolution.EndpointId;
                _activeDeviceName = handle.DeviceName ?? resolution.Name;

                return handle.Player;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠ Endpoint {EndpointId} could not be opened, falling back to default",
                    resolution.EndpointId);
            }
        }
        else if (!resolution.IsSystemDefault)
        {
            _logger.LogWarning("⚠ {Selection}", resolution.Reason);
        }

        return OpenDefaultOutput();
    }

    /// <summary>
    /// The fallback output. Recorded as its own backend so the health payload can say the saved
    /// endpoint is not what is playing, instead of implying the selection was honoured.
    /// </summary>
    private IWavePlayer OpenDefaultOutput()
    {
        var handle = _outputFactory.OpenDefault();
        _logger.LogInformation("Using default audio device: {DefaultDevice}", handle.DeviceName ?? "Unknown");

        _backend = handle.Backend;
        _activeEndpointId = handle.EndpointId;
        _activeDeviceName = handle.DeviceName;

        return handle.Player;
    }

    private void LogWaveOutConfiguration()
    {
        if (_waveOut == null)
        {
            _logger.LogError("WaveOut is null, cannot log configuration");
            return;
        }

        try
        {
            if (_waveOut is WaveOutEvent waveOutEvent)
            {
                // WaveOut device numbers are their own ordinal space: they do not index the WASAPI
                // render endpoint list, so the name is read from the WaveOut capabilities rather
                // than by using the device number as an index into the MMDevice collection.
                // WaveOutEvent exposes no capability helpers on this target framework, so the
                // WinMM entry points are called directly.
                try
                {
                    var deviceCount = WaveInterop.waveOutGetNumDevs();
                    var productName = "system default";

                    if (waveOutEvent.DeviceNumber >= 0 && waveOutEvent.DeviceNumber < deviceCount)
                    {
                        var capsResult = WaveInterop.waveOutGetDevCaps(
                            (IntPtr)waveOutEvent.DeviceNumber,
                            out var capabilities,
                            Marshal.SizeOf<WaveOutCapabilities>());

                        productName = capsResult == MmResult.NoError
                            ? capabilities.ProductName
                            : $"unknown (waveOutGetDevCaps: {capsResult})";
                    }

                    _logger.LogDebug(
                        "WaveOut Configuration - WaveOut device number {DeviceNumber} of {DeviceCount} is '{ProductName}', Volume: {Volume}",
                        waveOutEvent.DeviceNumber, deviceCount, productName, waveOutEvent.Volume);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to get device capabilities: {Error}", ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log WaveOut configuration");
        }
    }

    private void LogDetailedAudioError(Exception ex)
    {
        _logger.LogDebug("=== Detailed Audio Error Analysis ===");
        
        try
        {
            // Log exception details
            _logger.LogDebug("Exception Type: {ExceptionType}", ex.GetType().Name);
            _logger.LogDebug("Exception Message: {Message}", ex.Message);
            
            if (ex.InnerException != null)
            {
                _logger.LogDebug("Inner Exception: {InnerType} - {InnerMessage}", 
                    ex.InnerException.GetType().Name, ex.InnerException.Message);
            }

            // Log current system state
            _logger.LogDebug("Current WaveOut State: {WaveOutState}", _waveOut?.PlaybackState.ToString() ?? "null");
            _logger.LogDebug("Audio Engine Initialized: {IsInitialized}", _isInitialized);
            _logger.LogDebug("Active Effects Count: {ActiveCount}", _activeEffects.Count);
            
            // Check system audio availability through the same catalog the rest of the app uses.
            try
            {
                _logger.LogDebug("System render device count: {DeviceCount}", _deviceCatalog.GetDevices().Count);
            }
            catch (Exception deviceEx)
            {
                _logger.LogDebug("Failed to enumerate render devices: {Error}", deviceEx.Message);
            }
            
            // Log configuration that might cause issues
            _logger.LogDebug("Configured Device ID: {DeviceId}", _settings.Audio.AudioDeviceId);
            _logger.LogDebug("Configured Device Name: '{DeviceName}'", _settings.Audio.AudioDeviceName);
            _logger.LogDebug("Sample Rate: {SampleRate}Hz", _settings.Audio.SampleRate);
            _logger.LogDebug("Buffer Size: {BufferSize}", _settings.Audio.BufferSize);
            
        }
        catch (Exception logEx)
        {
            _logger.LogError(logEx, "Failed to log detailed audio error information");
        }
        
        _logger.LogDebug("=== End Detailed Audio Error Analysis ===");
    }

    /// <summary>
    /// One playing effect: the input the mixer is really reading from, the cancellation that stops
    /// its scheduled cleanup, and the handle to that scheduled cleanup. Held as a single registry
    /// entry so the three cannot get out of step with each other. All of its state is read and
    /// written under <see cref="_lock"/>.
    /// </summary>
    private sealed class ActiveEffect
    {
        private bool _disposed;

        public ActiveEffect(ReleaseEnvelopeSampleProvider mixerInput, CancellationTokenSource cancellation)
        {
            MixerInput = mixerInput;
            Cancellation = cancellation;
        }

        /// <summary>
        /// The actual mixer input, not a recast of it: removing an input and ramping it down both
        /// need the object the mixer is really reading from.
        /// </summary>
        public ReleaseEnvelopeSampleProvider MixerInput { get; }

        public CancellationTokenSource Cancellation { get; }

        /// <summary>
        /// The scheduled cleanup for this effect, assigned as soon as it has been started. Completed
        /// until then, so a caller that wants to observe the cleanup never waits on null.
        /// </summary>
        public Task Cleanup { get; set; } = Task.CompletedTask;

        /// <summary>
        /// Stops the scheduled cleanup. Tolerates an already-disposed source so the stop paths can
        /// run in any order without a torn-down effect throwing at them.
        /// </summary>
        public void CancelCleanup()
        {
            if (_disposed) return;

            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down by another path; there is nothing left to cancel.
            }
        }

        /// <summary>
        /// Cancels and releases the cancellation source exactly once, however many times it is
        /// called. Only the caller that removed this entry from the registry gets here.
        /// </summary>
        public void Discard()
        {
            if (_disposed) return;

            CancelCleanup();
            _disposed = true;
            Cancellation.Dispose();
        }
    }

    public void Dispose()
    {
        _logger.LogInformation("Disposing Audio Engine");
        
        StopAllEffects();

        _waveOut?.Stop();
        _waveOut?.Dispose();

        // StopAllEffects has normally emptied the registry already; this catches anything that
        // started while it was running and is a no-op otherwise.
        RemoveAllReleasedEffects();
    }
}

/// <summary>
/// A read of the audio output state that costs nothing: whether a device is open, whether opening
/// one failed and why, which device the settings ask for, which output is actually carrying audio,
/// and why the last playback did not reach it.
/// </summary>
public sealed record AudioEngineStatus(
    bool Initialized,
    bool InitializationFailed,
    string? LastError,
    string? ConfiguredDeviceName,
    DateTime? OpenedAtUtc,
    string? Backend = null,
    string? ActiveDeviceName = null,
    string? ActiveEndpointId = null,
    string? LastPlaybackError = null,
    DateTime? LastPlaybackAtUtc = null,
    int ActiveEffects = 0);

/// <summary>
/// Whether one playback attempt actually reached an open output, and why not when it did not.
/// A "test" that only scheduled work is not a success, so this is what the test endpoints report.
/// </summary>
public sealed record AudioPlaybackResult(bool Played, string? Error)
{
    public static AudioPlaybackResult Success { get; } = new(true, null);

    public static AudioPlaybackResult Failed(string error) => new(false, error);
}