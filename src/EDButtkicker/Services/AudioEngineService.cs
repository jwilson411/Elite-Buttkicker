using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
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
                return;

            try
            {
                _logger.LogInformation("Initializing Audio Engine");
                
                // Create wave output device
                if (_settings.Audio.AudioDeviceId >= 0)
                {
                    _waveOut = new WaveOutEvent { DeviceNumber = _settings.Audio.AudioDeviceId };
                    _logger.LogInformation("Using audio device {DeviceId}: {DeviceName}", 
                        _settings.Audio.AudioDeviceId, _settings.Audio.AudioDeviceName);
                }
                else
                {
                    _waveOut = new WaveOutEvent();
                    _logger.LogInformation("Using default audio device");
                }

                // Create mixer for combining multiple audio streams
                _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(_settings.Audio.SampleRate, 1));
                _mixer.ReadFully = true; // Ensure smooth playback

                // Start the output
                _waveOut.Init(_mixer);
                _waveOut.Play();

                _isInitialized = true;
                _logger.LogInformation("Audio Engine initialized successfully");
                _logger.LogInformation("Sample Rate: {SampleRate}Hz, Buffer Size: {BufferSize}", 
                    _settings.Audio.SampleRate, _settings.Audio.BufferSize);
                _logger.LogInformation("WaveOut PlaybackState: {PlaybackState}", _waveOut.PlaybackState);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize audio engine");
                throw;
            }
        }
    }

    public Task PlayHapticPattern(HapticPattern pattern, JournalEvent? journalEvent = null)
    {
        if (!_isInitialized)
        {
            _logger.LogWarning("Audio engine not initialized, skipping playback");
            return Task.CompletedTask;
        }

        try
        {
            var effectId = Guid.NewGuid().ToString();
            _logger.LogDebug("Playing haptic pattern: {PatternName} (ID: {EffectId})", pattern.Name, effectId);

            // Calculate intensity
            var intensity = CalculateIntensity(pattern, journalEvent);
            var frequency = pattern.Frequency;

            _logger.LogDebug("Pattern details - Frequency: {Frequency}Hz, Intensity: {Intensity}%, Duration: {Duration}ms",
                frequency, intensity, pattern.Duration);

            // Create appropriate sample provider based on pattern type
            ISampleProvider sampleProvider = pattern.Pattern switch
            {
                PatternType.MultiLayer => CreateMultiLayerPattern(pattern),
                PatternType.Sequence => CreateMultiLayerPattern(pattern), // Sequence uses same timing logic as MultiLayer
                _ => CreateStandardPattern(pattern, intensity, frequency)
            };

            lock (_lock)
            {
                // For compatibility, store the sample provider reference
                _activeGenerators[effectId] = sampleProvider as SignalGenerator ?? new SignalGenerator(_settings.Audio.SampleRate, 1);
                _mixer?.AddMixerInput(sampleProvider);
                _logger.LogDebug("Added sample provider to mixer. Active effects: {Count}", _activeGenerators.Count);
            }

            // Set up automatic cleanup
            var cancellationSource = new CancellationTokenSource();
            _activeCancellations[effectId] = cancellationSource;

            // Schedule cleanup after pattern duration
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(pattern.Duration + pattern.FadeOut + 100, cancellationSource.Token);
                    CleanupEffect(effectId);
                }
                catch (OperationCanceledException)
                {
                    // Expected when effect is manually stopped
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error playing haptic pattern: {PatternName}", pattern.Name);
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