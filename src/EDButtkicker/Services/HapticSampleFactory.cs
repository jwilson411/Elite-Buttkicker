using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using EDButtkicker.Models;

namespace EDButtkicker.Services;

/// <summary>
/// Builds the sample provider for a haptic pattern. Kept free of any audio device dependency so
/// the generation path can be exercised without opening WASAPI/WaveOut.
/// </summary>
public static class HapticSampleFactory
{
    /// <summary>
    /// Creates the fully assembled provider for a pattern and applies the app-level hardware
    /// safety limiter as the final stage for every pattern type.
    /// </summary>
    public static ISampleProvider Create(HapticPattern pattern, int intensity, int frequency, int sampleRate, int appMaxIntensity)
    {
        ISampleProvider sampleProvider = pattern.Pattern switch
        {
            PatternType.MultiLayer => CreateMultiLayerPattern(pattern, sampleRate),
            PatternType.Sequence => CreateMultiLayerPattern(pattern, sampleRate), // Sequence uses same timing logic as MultiLayer
            _ => CreateStandardPattern(pattern, intensity, frequency, sampleRate)
        };

        // Final app-level bound, after all mixing and envelopes.
        return AudioSafety.Limit(sampleProvider, appMaxIntensity);
    }

    public static ISampleProvider CreateMultiLayerPattern(HapticPattern pattern, int sampleRate)
    {
        return new MultiLayerPatternGenerator(pattern, sampleRate, 1);
    }

    public static ISampleProvider CreateStandardPattern(HapticPattern pattern, int intensity, int frequency, int sampleRate)
    {
        // Create base generator
        var generator = CreateSignalGenerator(intensity, frequency, sampleRate);

        // Apply envelope based on pattern type
        var sampleProvider = ApplyEnvelope(generator, pattern, sampleRate);

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

    public static SignalGenerator CreateSignalGenerator(int intensity, int frequency, int sampleRate)
    {
        var gain = Math.Clamp(intensity / 100.0, 0.0, 1.0);

        return new SignalGenerator(sampleRate, 1)
        {
            Gain = gain,
            Frequency = frequency,
            Type = SignalGeneratorType.Sin // Smooth sine wave for buttkicker
        };
    }

    private static ISampleProvider ApplyEnvelope(SignalGenerator generator, HapticPattern pattern, int sampleRate)
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
        sampleProvider = sampleProvider.Take(TimeSpan.FromMilliseconds(pattern.Duration));

        return sampleProvider;
    }

    private static ISampleProvider ApplySharpPulse(SignalGenerator generator, HapticPattern pattern)
    {
        // Quick attack, quick decay for sharp impacts
        // For now, just return the generator - envelope shaping will be implemented later
        return generator;
    }

    private static ISampleProvider ApplyBuildupRumble(SignalGenerator generator, HapticPattern pattern)
    {
        // Gradual buildup over the first 60% of duration, then sustain
        // For now, just return the generator - envelope shaping will be implemented later
        return generator;
    }

    private static ISampleProvider ApplySustainedRumble(SignalGenerator generator, HapticPattern pattern)
    {
        // Just apply basic fade envelope, maintain consistent output
        return generator;
    }

    private static ISampleProvider ApplyOscillating(SignalGenerator generator, HapticPattern pattern)
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

    private static ISampleProvider ApplyImpact(SignalGenerator generator, HapticPattern pattern)
    {
        // Sharp attack, longer decay
        // For now, just return the generator - envelope shaping will be implemented later
        return generator;
    }

    private static ISampleProvider ApplyFade(SignalGenerator generator, HapticPattern pattern)
    {
        // Gentle fade in and out
        // For now, just return the generator - envelope shaping will be implemented later
        return generator;
    }
}
