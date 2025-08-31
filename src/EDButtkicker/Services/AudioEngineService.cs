using Microsoft.Extensions.Logging;
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
                if (_settings.Audio.AudioDeviceId >= 0)
                {
                    _logger.LogDebug("Attempting to use specific audio device - ID: {DeviceId}, Name: '{DeviceName}'", 
                        _settings.Audio.AudioDeviceId, _settings.Audio.AudioDeviceName);
                    
                    // Validate device still exists
                    if (ValidateAudioDevice(_settings.Audio.AudioDeviceId))
                    {
                        _waveOut = new WaveOutEvent { DeviceNumber = _settings.Audio.AudioDeviceId };
                        _logger.LogInformation("✓ Successfully configured audio device {DeviceId}: {DeviceName}", 
                            _settings.Audio.AudioDeviceId, _settings.Audio.AudioDeviceName);
                    }
                    else
                    {
                        _logger.LogWarning("⚠ Configured audio device {DeviceId} '{DeviceName}' not available, falling back to default", 
                            _settings.Audio.AudioDeviceId, _settings.Audio.AudioDeviceName);
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

    public Task PlayHapticPattern(HapticPattern pattern, JournalEvent? journalEvent = null)
    {
        if (!_isInitialized)
        {
            _logger.LogWarning("⚠ Audio engine not initialized, skipping playbook for pattern: {PatternName}", pattern.Name);
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

            // Create appropriate sample provider based on pattern type
            ISampleProvider sampleProvider = pattern.Pattern switch
            {
                PatternType.MultiLayer => CreateMultiLayerPattern(pattern),
                PatternType.Sequence => CreateMultiLayerPattern(pattern), // Sequence uses same timing logic as MultiLayer
                _ => CreateStandardPattern(pattern, intensity, frequency)
            };

            _logger.LogDebug("Created sample provider type: {SampleProviderType}", sampleProvider.GetType().Name);

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

    private SignalGenerator CreateSignalGenerator(HapticPattern pattern, int intensity, int frequency)
    {
        var gain = intensity / 100.0;
        // Temporarily boost gain to debug audio issues
        var boostedGain = Math.Min(gain * 2.0, 1.0); // Double the gain but cap at 1.0
        
        var generator = new SignalGenerator(_settings.Audio.SampleRate, 1)
        {
            Gain = boostedGain,
            Frequency = frequency,
            Type = SignalGeneratorType.Sin // Smooth sine wave for buttkicker
        };

        _logger.LogDebug("Created signal generator - Intensity: {Intensity}%, Gain: {Gain}, Frequency: {Frequency}Hz, SampleRate: {SampleRate}", 
            intensity, gain, frequency, _settings.Audio.SampleRate);

        return generator;
    }

    private ISampleProvider ApplyEnvelope(SignalGenerator generator, HapticPattern pattern)
    {
        ISampleProvider sampleProvider = generator;

        // Apply pattern-specific modifications
        switch (pattern.Pattern)
        {
            case PatternType.SharpPulse:
                sampleProvider = ApplySharpPulse(generator, pattern);
                break;
                
            case PatternType.BuildupRumble:
                sampleProvider = ApplyBuildupRumble(generator, pattern);
                break;
                
            case PatternType.SustainedRumble:
                sampleProvider = ApplySustainedRumble(generator, pattern);
                break;
                
            case PatternType.Oscillating:
                sampleProvider = ApplyOscillating(generator, pattern);
                break;
                
            case PatternType.Impact:
                sampleProvider = ApplyImpact(generator, pattern);
                break;
                
            case PatternType.Fade:
                sampleProvider = ApplyFade(generator, pattern);
                break;
        }

        // Apply overall fade in/out envelope
        if (pattern.FadeIn > 0 || pattern.FadeOut > 0)
        {
            // For now, just use the base sample provider - fade will be implemented later
            // sampleProvider = ApplyFadeEnvelope(sampleProvider, pattern);
        }

        // Limit duration
        var durationSamples = (int)(_settings.Audio.SampleRate * (pattern.Duration / 1000.0));
        sampleProvider = sampleProvider.Take(TimeSpan.FromMilliseconds(pattern.Duration));

        return sampleProvider;
    }

    private ISampleProvider ApplySharpPulse(SignalGenerator generator, HapticPattern pattern)
    {
        // Quick attack, quick decay for sharp impacts
        // For now, just return the generator - envelope shaping will be implemented later
        return generator;
    }

    private ISampleProvider ApplyBuildupRumble(SignalGenerator generator, HapticPattern pattern)
    {
        // Gradual buildup over the first 60% of duration, then sustain
        // For now, just return the generator - envelope shaping will be implemented later
        return generator;
    }

    private ISampleProvider ApplySustainedRumble(SignalGenerator generator, HapticPattern pattern)
    {
        // Just apply basic fade envelope, maintain consistent output
        return generator;
    }

    private ISampleProvider ApplyOscillating(SignalGenerator generator, HapticPattern pattern)
    {
        // Create oscillating amplitude effect with different rates for different events
        var oscFreq = pattern.Name switch
        {
            "Overheating Warning" => 3.0, // Fast oscillation for heat warnings
            "Heat Damage" => 5.0, // Very fast for heat damage
            "Being Interdicted" => 2.5, // Medium for interdiction
            "Neutron Boost" => 1.5, // Slow deep rumble for neutron stars
            _ => 2.0 // Default oscillation rate
        };
        
        var modulationDepth = pattern.Name switch
        {
            "Heat Damage" => 0.8f, // Deep modulation for damage
            "Overheating Warning" => 0.6f, // Moderate for warnings
            "Being Interdicted" => 0.7f, // Strong for interdiction stress
            "Neutron Boost" => 0.4f, // Gentle for neutron boost
            _ => 0.5f // Default depth
        };
        
        return new AmplitudeModulationSampleProvider(generator, oscFreq, modulationDepth);
    }

    private ISampleProvider ApplyImpact(SignalGenerator generator, HapticPattern pattern)
    {
        // Sharp attack, longer decay
        // For now, just return the generator - envelope shaping will be implemented later
        return generator;
    }

    private ISampleProvider ApplyFade(SignalGenerator generator, HapticPattern pattern)
    {
        // Gentle fade in and out
        // For now, just return the generator - envelope shaping will be implemented later
        return generator;
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

    private ISampleProvider CreateMultiLayerPattern(HapticPattern pattern)
    {
        return new MultiLayerPatternGenerator(pattern, _settings.Audio.SampleRate, 1);
    }

    private ISampleProvider CreateStandardPattern(HapticPattern pattern, int intensity, int frequency)
    {
        // Create base generator
        var generator = CreateSignalGenerator(pattern, intensity, frequency);
        
        // Apply envelope based on pattern type
        var sampleProvider = ApplyEnvelope(generator, pattern);
        
        // Apply intensity curve if specified
        if (pattern.IntensityCurve != IntensityCurve.Linear)
        {
            sampleProvider = new CurveEnvelopeSampleProvider(
                sampleProvider, 
                pattern.IntensityCurve, 
                pattern.Duration, 
                intensity / 100.0f, 
                pattern.CustomCurvePoints
            );
        }
        
        return sampleProvider;
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
                _logger.LogDebug("WaveOut Configuration - Device Number: {DeviceNumber}, Volume: {Volume}", 
                    waveOutEvent.DeviceNumber, waveOutEvent.Volume);
                
                // Get device capabilities using MMDevice API
                try
                {
                    var deviceEnumerator = new MMDeviceEnumerator();
                    var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    
                    if (waveOutEvent.DeviceNumber >= 0 && waveOutEvent.DeviceNumber < devices.Count)
                    {
                        var device = devices[waveOutEvent.DeviceNumber];
                        _logger.LogDebug("Target Device Capabilities - Name: '{FriendlyName}', ID: {DeviceId}",
                            device.FriendlyName, device.ID);
                    }
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