using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
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
    private readonly Dictionary<string, SignalGenerator> _activeGenerators = new();
    private readonly Dictionary<string, CancellationTokenSource> _activeCancellations = new();

    public AudioEngineService(ILogger<AudioEngineService> logger, AppSettings settings)
    {
        _logger = logger;
        _settings = settings;
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
                _activeGenerators.Count);
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

        // Check if wave output is still valid
        if (_waveOut == null)
        {
            _logger.LogError("❌ Wave output is null, cannot play pattern: {PatternName}", pattern.Name);
            return Task.FromResult(RecordPlaybackFailure("The audio output was closed, so nothing could be played."));
        }

        var playbackState = _waveOut.PlaybackState;
        if (playbackState != PlaybackState.Playing)
        {
            _logger.LogWarning("⚠ Wave output not in playing state ({PlaybackState}), attempting to restart for pattern: {PatternName}",
                playbackState, pattern.Name);
            try
            {
                _waveOut.Play();
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
                    _activeGenerators.Count, _mixer?.MixerInputs?.Count() ?? 0);
            }

            // Create appropriate sample provider based on pattern type.
            // The factory applies the app-level hardware safety limiter as the final stage for
            // every pattern type, so nothing reaches the mixer above Audio.MaxIntensity.
            ISampleProvider sampleProvider = HapticSampleFactory.Create(
                pattern, intensity, frequency, _settings.Audio.SampleRate, _settings.Audio.MaxIntensity);

            _logger.LogDebug("Created sample provider type: {SampleProviderType}, limited to {MaxIntensity}% max intensity",
                sampleProvider.GetType().Name, _settings.Audio.MaxIntensity);

            lock (_lock)
            {
                // For compatibility, store the sample provider reference
                _activeGenerators[effectId] = sampleProvider as SignalGenerator ?? new SignalGenerator(_settings.Audio.SampleRate, 1);
                
                try
                {
                    _mixer?.AddMixerInput(sampleProvider);
                    _logger.LogDebug("✓ Added sample provider to mixer successfully. Active effects: {Count}", _activeGenerators.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Failed to add sample provider to mixer");
                    throw;
                }
            }

            // Set up automatic cleanup
            var cancellationSource = new CancellationTokenSource();
            _activeCancellations[effectId] = cancellationSource;

            var cleanupDelay = pattern.Duration + pattern.FadeOut + 100;
            _logger.LogDebug("Scheduling cleanup for effect {EffectId} in {CleanupDelay}ms", effectId, cleanupDelay);

            // Schedule cleanup after pattern duration
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(cleanupDelay, cancellationSource.Token);
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

    private void CleanupEffect(string effectId)
    {
        lock (_lock)
        {
            if (_activeGenerators.TryGetValue(effectId, out var generator))
            {
                try
                {
                    _mixer?.RemoveMixerInput(generator);
                    _activeGenerators.Remove(effectId);
                    _logger.LogDebug("Cleaned up audio effect: {EffectId}", effectId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error cleaning up audio effect: {EffectId}", effectId);
                }
            }

            if (_activeCancellations.TryGetValue(effectId, out var cancellation))
            {
                cancellation.Cancel();
                cancellation.Dispose();
                _activeCancellations.Remove(effectId);
            }
        }
    }

    /// <summary>
    /// Silences everything that is playing right now and returns how many effects were stopped.
    /// This is the panic button behind the UI's stop control, so it takes effect immediately rather
    /// than waiting for the scheduled cleanup of each effect.
    /// </summary>
    public virtual int StopAllEffects()
    {
        lock (_lock)
        {
            var stopped = _activeGenerators.Count;
            _logger.LogInformation("Stopping all active audio effects ({Count})", stopped);

            foreach (var cancellation in _activeCancellations.Values)
            {
                cancellation.Cancel();
            }

            _activeGenerators.Clear();
            _activeCancellations.Clear();

            // Clear mixer
            _mixer?.RemoveAllMixerInputs();

            return stopped;
        }
    }

    public void Reinitialize()
    {
        _logger.LogInformation("Reinitializing Audio Engine with new device settings");
        
        lock (_lock)
        {
            // Stop and dispose current audio engine
            StopAllEffects();
            
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
            
            // Clear active cancellations
            foreach (var cancellation in _activeCancellations.Values)
            {
                cancellation.Cancel();
                cancellation.Dispose();
            }
            _activeCancellations.Clear();
            _activeGenerators.Clear();
            
            // Initialize with new settings
            Initialize();
        }
    }

    private void LogSystemAudioInfo()
    {
        try
        {
            _logger.LogDebug("=== System Audio Information ===");
            
            // Log WASAPI devices using MMDeviceEnumerator (compatible with NAudio 2.2.1)
            try
            {
                var deviceEnumerator = new MMDeviceEnumerator();
                var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                
                _logger.LogDebug("WASAPI Active Render Devices: {Count}", devices.Count);
                for (int i = 0; i < devices.Count; i++)
                {
                    var device = devices[i];
                    _logger.LogDebug("WASAPI Device {Index}: '{FriendlyName}' - ID: {DeviceId}, State: {State}", 
                        i, device.FriendlyName, device.ID, device.State);
                }

                var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _logger.LogDebug("Default WASAPI Device: '{FriendlyName}' - ID: {DeviceId}", 
                    defaultDevice.FriendlyName, defaultDevice.ID);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to enumerate WASAPI devices: {Error}", ex.Message);
            }
            
            _logger.LogDebug("=== End System Audio Information ===");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging system audio information");
        }
    }

    private string? GetDefaultAudioDevice()
    {
        try
        {
            var deviceEnumerator = new MMDeviceEnumerator();
            var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return defaultDevice.FriendlyName;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to get default audio device name: {Error}", ex.Message);
            return null;
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
        MMDeviceEnumerator enumerator;
        IReadOnlyList<AudioDevice> enumerated;

        try
        {
            enumerator = new MMDeviceEnumerator();
            enumerated = EnumerateRenderEndpoints(enumerator);
        }
        catch (Exception ex)
        {
            // No WASAPI here: the default output is the only thing left to try.
            _logger.LogWarning(ex, "Failed to enumerate WASAPI render endpoints, using the default output");
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
                var endpoint = enumerator.GetDevice(resolution.EndpointId);
                _logger.LogInformation("✓ Using audio device: {DeviceName} (endpoint {EndpointId})",
                    endpoint.FriendlyName, resolution.EndpointId);

                var output = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 200);
                _backend = "WASAPI";
                _activeEndpointId = resolution.EndpointId;
                _activeDeviceName = endpoint.FriendlyName;

                return output;
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
        var defaultDevice = GetDefaultAudioDevice();
        _logger.LogInformation("Using default audio device: {DefaultDevice}", defaultDevice ?? "Unknown");

        _backend = "WaveOut";
        _activeEndpointId = null;
        _activeDeviceName = defaultDevice;

        return new WaveOutEvent();
    }

    /// <summary>Active render endpoints as plain models, each carrying its endpoint id.</summary>
    private IReadOnlyList<AudioDevice> EnumerateRenderEndpoints(MMDeviceEnumerator enumerator)
    {
        string? defaultEndpointId = null;
        try
        {
            defaultEndpointId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("No default render endpoint available: {Error}", ex.Message);
        }

        var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var enumerated = new List<AudioDevice>(endpoints.Count);

        for (var i = 0; i < endpoints.Count; i++)
        {
            var endpoint = endpoints[i];
            enumerated.Add(new AudioDevice
            {
                EndpointId = endpoint.ID,
                DeviceId = i,
                Name = endpoint.FriendlyName,
                Driver = "WASAPI",
                Channels = 2,
                IsDefault = defaultEndpointId != null && endpoint.ID == defaultEndpointId,
                IsAvailable = endpoint.State == DeviceState.Active
            });
        }

        return enumerated;
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
            _logger.LogDebug("Active Effects Count: {ActiveCount}", _activeGenerators.Count);
            
            // Check system audio availability using MMDevice API
            try
            {
                var deviceEnumerator = new MMDeviceEnumerator();
                var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                _logger.LogDebug("System WASAPI Device Count: {DeviceCount}", devices.Count);
            }
            catch (Exception deviceEx)
            {
                _logger.LogDebug("Failed to enumerate WASAPI devices: {Error}", deviceEx.Message);
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

    public void Dispose()
    {
        _logger.LogInformation("Disposing Audio Engine");
        
        StopAllEffects();
        
        _waveOut?.Stop();
        _waveOut?.Dispose();
        
        foreach (var cancellation in _activeCancellations.Values)
        {
            cancellation.Dispose();
        }
        _activeCancellations.Clear();
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