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
                // Create wave output device
				if (!string.IsNullOrEmpty(_settings.Audio.AudioDeviceName))
				{
					_logger.LogDebug("Attempting to find audio device by name: '{DeviceName}'", _settings.Audio.AudioDeviceName);
					
					var deviceEnumerator = new MMDeviceEnumerator();
					var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
					LogWasapiEnumeration(devices);
					var matchedDevice = devices.FirstOrDefault(d => d.FriendlyName == _settings.Audio.AudioDeviceName);

					if (matchedDevice != null)
					{
						_waveOut = new WasapiOut(matchedDevice, AudioClientShareMode.Shared, true, 200);
						_logger.LogInformation("✓ Using audio device: {DeviceName}", matchedDevice.FriendlyName);
					}
					else
					{
						_logger.LogWarning("⚠ Device '{DeviceName}' not found, falling back to default", _settings.Audio.AudioDeviceName);
						_waveOut = new WaveOutEvent();
					}
				}
				else
				{
					_waveOut = new WaveOutEvent();
					var defaultDevice = GetDefaultAudioDevice();
					_logger.LogInformation("Using default audio device: {DefaultDevice}", defaultDevice ?? "Unknown");
				}

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
                _logger.LogInformation("✓ Audio Engine initialized successfully");
                _logger.LogInformation("Configuration: Sample Rate: {SampleRate}Hz, Buffer Size: {BufferSize}, Channels: 1", 
                    _settings.Audio.SampleRate, _settings.Audio.BufferSize);
                _logger.LogInformation("WaveOut PlaybackState: {PlaybackState}", _waveOut.PlaybackState);
            }
            catch (Exception ex)
            {
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
    public bool EnsureInitialized()
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
                _logger.LogError(ex, "Audio engine unavailable, haptics are disabled for this session");
                return false;
            }

            return _isInitialized;
        }
    }

    /// <summary>
    /// Plays one pattern. Virtual so tests can observe what would be played without opening a device;
    /// when no audio device is available this degrades to a no-op instead of throwing.
    /// </summary>
    public virtual Task PlayHapticPattern(HapticPattern pattern, JournalEvent? journalEvent = null)
    {
        if (!EnsureInitialized())
        {
            _logger.LogWarning("⚠ Audio engine not initialized, skipping playback for pattern: {PatternName}", pattern.Name);
            return Task.CompletedTask;
        }

        // Check if wave output is still valid
        if (_waveOut == null)
        {
            _logger.LogError("❌ Wave output is null, cannot play pattern: {PatternName}", pattern.Name);
            return Task.CompletedTask;
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
                return Task.CompletedTask;
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
        }
        
        return Task.CompletedTask;
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

    public void StopAllEffects()
    {
        lock (_lock)
        {
            _logger.LogInformation("Stopping all active audio effects");
            
            foreach (var cancellation in _activeCancellations.Values)
            {
                cancellation.Cancel();
            }

            _activeGenerators.Clear();
            _activeCancellations.Clear();
            
            // Clear mixer
            _mixer?.RemoveAllMixerInputs();
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
            // A new device deserves a fresh attempt even if the previous one could not be opened.
            _initializationFailed = false;
            
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

    private bool ValidateAudioDevice(int deviceId)
    {
        try
        {
            // Use MMDevice API for validation (compatible with NAudio 2.2.1)
            var deviceEnumerator = new MMDeviceEnumerator();
            var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            
            if (deviceId < 0 || deviceId >= devices.Count)
            {
                _logger.LogWarning("Device ID {DeviceId} is out of range (0-{MaxId})", deviceId, devices.Count - 1);
                return false;
            }

            var device = devices[deviceId];
            _logger.LogDebug("Validated device {DeviceId}: '{FriendlyName}' - Available", deviceId, device.FriendlyName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate audio device {DeviceId}: {Error}", deviceId, ex.Message);
            return false;
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
    /// Logs every WASAPI render endpoint with both its list position and its DeviceId, then what the
    /// saved settings resolve to. This is the record to read when a device selection appears to land
    /// on the neighbouring device: it shows whether the saved name and the saved DeviceId disagree.
    /// </summary>
    private void LogWasapiEnumeration(MMDeviceCollection devices)
    {
        try
        {
            string? defaultDeviceId = null;
            try
            {
                defaultDeviceId = new MMDeviceEnumerator()
                    .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("No default render endpoint available: {Error}", ex.Message);
            }

            var enumerated = new List<AudioDevice>(devices.Count);
            for (var i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                enumerated.Add(new AudioDevice
                {
                    DeviceId = i,
                    Name = device.FriendlyName,
                    Driver = "WASAPI",
                    IsDefault = defaultDeviceId != null && device.ID == defaultDeviceId,
                    IsAvailable = device.State == DeviceState.Active
                });
            }

            foreach (var line in AudioDeviceDiagnostics.DescribeEnumeration(enumerated, "WASAPI render"))
            {
                _logger.LogInformation("Audio devices - {Device}", line);
            }

            _logger.LogInformation("Audio devices - {Selection}", AudioDeviceDiagnostics.DescribeConfiguredSelection(
                enumerated, _settings.Audio.AudioDeviceId, _settings.Audio.AudioDeviceName));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log WASAPI device enumeration");
        }
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