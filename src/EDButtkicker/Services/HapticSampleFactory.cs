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

    /// <summary>
    /// MultiLayer and Sequence. The generator already applies each layer's own StartTime/FadeIn/
    /// FadeOut, so the pattern-level fades (and the click-prevention floor) are applied on top of the
    /// finished mix - the whole pattern starts and ends at zero even when a layer does not.
    /// </summary>
    public static ISampleProvider CreateMultiLayerPattern(HapticPattern pattern, int sampleRate)
    {
        var layered = new MultiLayerPatternGenerator(pattern, sampleRate, 1);

        return Shaped(layered, pattern, 0, 0);
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

    /// <summary>
    /// Applies the pattern-specific attack/sustain/decay shape. Every branch ends in an
    /// <see cref="EnvelopeSampleProvider"/>, which also owns the pattern duration, so the stream is
    /// exactly Duration long and its first and last samples are at (or heading to) zero.
    /// </summary>
    private static ISampleProvider ApplyEnvelope(SignalGenerator generator, HapticPattern pattern, int sampleRate)
    {
        return pattern.Pattern switch
        {
            PatternType.SharpPulse => ApplySharpPulse(generator, pattern),
            PatternType.BuildupRumble => ApplyBuildupRumble(generator, pattern),
            PatternType.SustainedRumble => ApplySustainedRumble(generator, pattern),
            PatternType.Oscillating => ApplyOscillating(generator, pattern),
            PatternType.Impact => ApplyImpact(generator, pattern),
            PatternType.Fade => ApplyFade(generator, pattern),
            // Anything else still gets its duration and its click-prevention ramps.
            _ => Shaped(generator, pattern, 0, 0)
        };
    }

    /// <summary>
    /// Wraps a shaped source in the sample-accurate envelope. The pattern's own FadeIn/FadeOut
    /// compose with the shape by taking the quieter gain - for linear ramps that is simply the
    /// longer of the two - and both ends are floored at a short click-prevention ramp so no pattern
    /// type can start or stop at full amplitude.
    /// </summary>
    private static ISampleProvider Shaped(ISampleProvider source, HapticPattern pattern, double attackMs, double decayMs)
    {
        var floor = HapticEnvelope.MinimumRampMilliseconds(pattern.Frequency);
        var attack = Math.Max(Math.Max(attackMs, pattern.FadeIn), floor);
        var decay = Math.Max(Math.Max(decayMs, pattern.FadeOut), floor);

        return new EnvelopeSampleProvider(source, pattern.Duration, attack, decay);
    }

    private static ISampleProvider ApplySharpPulse(SignalGenerator generator, HapticPattern pattern)
    {
        // Quick attack, short sustain, fast decay - the click is the point, so the ramps stay short.
        var attack = Math.Max(2.0, pattern.Duration * 0.05);
        var decay = pattern.Duration * 0.30;

        return Shaped(generator, pattern, attack, decay);
    }

    private static ISampleProvider ApplyBuildupRumble(SignalGenerator generator, HapticPattern pattern)
    {
        // Gradual buildup over the first 60% of duration, then sustain, then the tail fade.
        return Shaped(generator, pattern, pattern.Duration * 0.60, 0);
    }

    private static ISampleProvider ApplySustainedRumble(SignalGenerator generator, HapticPattern pattern)
    {
        // Fade in to full, hold, fade out. Without explicit fades a 20ms ramp keeps the hold clean.
        const double defaultFadeMs = 20.0;

        return Shaped(
            generator,
            pattern,
            pattern.FadeIn > 0 ? pattern.FadeIn : defaultFadeMs,
            pattern.FadeOut > 0 ? pattern.FadeOut : defaultFadeMs);
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

        // The modulation is free-running, so without the wrapping ramps the effect would start and
        // stop at whatever amplitude the modulator happened to be at.
        var modulated = new AmplitudeModulationSampleProvider(generator, oscFreq, modulationDepth);

        return Shaped(modulated, pattern, 0, 0);
    }

    private static ISampleProvider ApplyImpact(SignalGenerator generator, HapticPattern pattern)
    {
        // Classic percussion: short attack, then the rest of the duration is decay.
        var attack = Math.Max(2.0, pattern.Duration * 0.08);
        var decay = Math.Max(0.0, pattern.Duration - attack);

        return Shaped(generator, pattern, attack, decay);
    }

    private static ISampleProvider ApplyFade(SignalGenerator generator, HapticPattern pattern)
    {
        // Gentle in and out; without explicit fades, a fifth of the duration at each end.
        return Shaped(
            generator,
            pattern,
            pattern.FadeIn > 0 ? pattern.FadeIn : pattern.Duration * 0.20,
            pattern.FadeOut > 0 ? pattern.FadeOut : pattern.Duration * 0.20);
    }
}
