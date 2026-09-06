using EDButtkicker.Models;
using EDButtkicker.Services;
using NAudio.Wave;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Every pattern type has to start at zero and end at zero, run for exactly its declared duration,
/// and a cancellation has to ramp to zero inside a bounded interval rather than cutting the signal.
/// Sample providers only - no WASAPI, no WaveOut, no real device, so nothing here says anything
/// about what the hardware does.
/// </summary>
public class FadeEnvelopeTests
{
    private const int SampleRate = 44100;
    private const int DurationMs = 200;
    private const int Frequency = 40;
    private const int Intensity = 80;

    /// <summary>One mixer buffer: the tolerance the issue allows on generated stream length.</summary>
    private const int BufferSamples = 1024;

    /// <summary>A ramp of a few ms leaves the edge samples this far from silence at most.</summary>
    private const float EdgeEpsilon = 0.02f;

    public static TheoryData<PatternType> AllPatternTypes => new()
    {
        PatternType.SharpPulse,
        PatternType.BuildupRumble,
        PatternType.SustainedRumble,
        PatternType.Oscillating,
        PatternType.Impact,
        PatternType.Fade,
        PatternType.MultiLayer,
        PatternType.Sequence
    };

    [Theory]
    [MemberData(nameof(AllPatternTypes))]
    public void EveryPatternType_StartsAndEndsNearZero(PatternType type)
    {
        var samples = Render(BuildPattern(type));

        Assert.NotEmpty(samples);
        Assert.True(Math.Abs(samples[0]) <= EdgeEpsilon,
            $"{type} started at {samples[0]} instead of near zero.");
        Assert.True(Math.Abs(samples[^1]) <= EdgeEpsilon,
            $"{type} ended at {samples[^1]} instead of near zero.");
    }

    [Theory]
    [MemberData(nameof(AllPatternTypes))]
    public void EveryPatternType_RunsForExactlyItsDurationAndIsNotSilentInBetween(PatternType type)
    {
        var samples = Render(BuildPattern(type));

        var expected = (int)Math.Round(DurationMs / 1000.0 * SampleRate);
        Assert.InRange(samples.Count, expected - BufferSamples, expected + BufferSamples);

        // A silent provider would satisfy the start/end assertions, so demand real signal.
        Assert.True(PeakOf(samples, samples.Count / 4, samples.Count / 2) > 0.05f,
            $"{type} produced no signal between the ramps.");
    }

    [Theory]
    [MemberData(nameof(AllPatternTypes))]
    public void EveryPatternType_HonoursExplicitFadeInAndFadeOut(PatternType type)
    {
        var pattern = BuildPattern(type);
        pattern.FadeIn = 50;
        pattern.FadeOut = 50;

        var samples = Render(pattern);
        var tenMs = SampleRate / 100;

        var head = PeakOf(samples, 0, tenMs);
        var middle = PeakOf(samples, samples.Count / 2 - tenMs, samples.Count / 2 + tenMs);
        var tail = PeakOf(samples, samples.Count - tenMs, samples.Count);

        Assert.True(head < middle, $"{type} first 10ms ({head}) was not quieter than the middle ({middle}).");
        Assert.True(tail < middle, $"{type} last 10ms ({tail}) was not quieter than the middle ({middle}).");
    }

    [Fact]
    public void SustainedRumble_WithNoExplicitFades_StillRampsBothEnds()
    {
        // The default 20ms ramps: the hold is at full amplitude but neither edge is.
        var samples = Render(BuildPattern(PatternType.SustainedRumble));
        var tenMs = SampleRate / 100;

        var middle = PeakOf(samples, samples.Count / 2 - tenMs, samples.Count / 2 + tenMs);

        Assert.True(PeakOf(samples, 0, tenMs) < middle);
        Assert.True(PeakOf(samples, samples.Count - tenMs, samples.Count) < middle);
    }

    [Fact]
    public void BuildupRumble_BuildsUpOverTheFirstPartOfTheDuration()
    {
        var samples = Render(BuildPattern(PatternType.BuildupRumble));

        var firstQuarter = PeakOf(samples, 0, samples.Count / 4);
        var lastHalf = PeakOf(samples, samples.Count / 2, samples.Count);

        Assert.True(firstQuarter < lastHalf,
            $"BuildupRumble did not build up: first quarter {firstQuarter} vs last half {lastHalf}.");
    }

    [Fact]
    public void Impact_DecaysThroughoutWhereSharpPulseHoldsThenDrops()
    {
        // Same duration, different shapes. The pulse holds full amplitude until its last 30% and
        // only then decays; the impact spends everything after its short attack decaying. So by the
        // third quarter the impact is well down its ramp while the pulse is still at full - and
        // both are back at zero on the last sample.
        var pulse = Render(BuildPattern(PatternType.SharpPulse));
        var impact = Render(BuildPattern(PatternType.Impact));

        var impactEarly = PeakOf(impact, 0, impact.Count / 4);
        var impactLate = PeakOf(impact, impact.Count / 2, impact.Count * 3 / 4);
        var pulseEarly = PeakOf(pulse, 0, pulse.Count / 4);
        var pulseLate = PeakOf(pulse, pulse.Count / 2, pulse.Count * 3 / 4);

        Assert.True(impactLate < impactEarly,
            $"Impact was not decaying: third quarter {impactLate} vs first quarter {impactEarly}.");
        Assert.True(pulseLate >= pulseEarly * 0.95f,
            $"SharpPulse was not still holding: third quarter {pulseLate} vs first quarter {pulseEarly}.");
        Assert.True(impactLate < pulseLate,
            $"Impact third quarter ({impactLate}) was not below the still-held SharpPulse ({pulseLate}).");
        Assert.True(Math.Abs(pulse[^1]) <= EdgeEpsilon);
        Assert.True(Math.Abs(impact[^1]) <= EdgeEpsilon);
    }

    [Fact]
    public void Envelope_ClampsAttackAndDecayToTheDurationInsteadOfOverlappingLoudly()
    {
        // Fades far longer than the pattern: the shape has to fit inside the duration, and the
        // stream still has to be exactly the pattern length.
        var pattern = BuildPattern(PatternType.SustainedRumble);
        pattern.Duration = 40;
        pattern.FadeIn = 500;
        pattern.FadeOut = 500;

        var provider = new EnvelopeSampleProvider(
            HapticSampleFactory.CreateSignalGenerator(100, Frequency, SampleRate),
            pattern.Duration,
            pattern.FadeIn,
            pattern.FadeOut);

        Assert.Equal((int)Math.Round(40 / 1000.0 * SampleRate), provider.TotalSamples);
        Assert.True(provider.AttackSamples + provider.DecaySamples <= provider.TotalSamples);
        Assert.Equal(0f, provider.GainAt(0));
        Assert.Equal(0f, provider.GainAt(provider.TotalSamples));

        for (var i = 0; i < provider.TotalSamples; i++)
        {
            Assert.InRange(provider.GainAt(i), 0f, 1f);
        }
    }

    [Fact]
    public void ReleaseEnvelope_RampsToZeroWithinItsBoundAndStaysThere()
    {
        var source = new ConstantSampleProvider(SampleRate, 1.0f);
        var release = new ReleaseEnvelopeSampleProvider(source, HapticEnvelope.ReleaseMilliseconds);
        var buffer = new float[512];

        // Untouched before the release: this is the audio the mixer would be playing.
        var read = release.Read(buffer, 0, buffer.Length);
        Assert.Equal(buffer.Length, read);
        Assert.All(buffer.Take(read), s => Assert.Equal(1.0f, s, 1e-6f));
        Assert.False(release.IsReleasing);

        release.BeginRelease();
        Assert.True(release.IsReleasing);

        var bound = (int)Math.Round(HapticEnvelope.ReleaseMilliseconds / 1000.0 * SampleRate);
        var ramp = new List<float>();
        while (ramp.Count < bound * 3)
        {
            read = release.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                // End of stream after the ramp is silence as far as the mixer is concerned.
                while (ramp.Count < bound * 3) ramp.Add(0f);
                break;
            }

            ramp.AddRange(buffer.Take(read));
        }

        // Monotonically falling, never rising back up.
        for (var i = 1; i < bound && i < ramp.Count; i++)
        {
            Assert.True(ramp[i] <= ramp[i - 1] + 1e-6f,
                $"Release gain rose at sample {i}: {ramp[i - 1]} -> {ramp[i]}.");
        }

        // Zero by the bound, and zero for good afterwards.
        Assert.True(ramp[bound - 1] <= 0.01f, $"Gain was still {ramp[bound - 1]} at the release bound.");
        for (var i = bound; i < ramp.Count; i++)
        {
            Assert.True(Math.Abs(ramp[i]) <= 1e-6f, $"Sample {i} after the release bound was {ramp[i]}.");
        }

        Assert.True(release.IsReleaseComplete);
    }

    [Fact]
    public void ReleaseEnvelope_LeavesTheSignalAloneUntilItIsCancelled()
    {
        var release = new ReleaseEnvelopeSampleProvider(new ConstantSampleProvider(SampleRate, 0.5f));
        var buffer = new float[HapticEnvelope.ReleaseMilliseconds * SampleRate / 1000 * 4];

        var read = release.Read(buffer, 0, buffer.Length);

        Assert.Equal(buffer.Length, read);
        Assert.All(buffer, s => Assert.Equal(0.5f, s, 1e-6f));
        Assert.False(release.IsReleaseComplete);
    }

    private static HapticPattern BuildPattern(PatternType type) => new()
    {
        Name = $"{type} Envelope Test",
        Pattern = type,
        Frequency = Frequency,
        Duration = DurationMs,
        Intensity = Intensity,
        MaxIntensity = 100,
        Waveform = WaveformType.Sine
    };

    private static List<float> Render(HapticPattern pattern) =>
        ReadAll(HapticSampleFactory.Create(pattern, pattern.Intensity, pattern.Frequency, SampleRate, 100));

    private static float PeakOf(IReadOnlyList<float> samples, int start, int end)
    {
        var peak = 0f;
        for (var i = Math.Max(0, start); i < Math.Min(end, samples.Count); i++)
        {
            peak = Math.Max(peak, Math.Abs(samples[i]));
        }

        return peak;
    }

    private static List<float> ReadAll(ISampleProvider provider)
    {
        var samples = new List<float>();
        var buffer = new float[BufferSamples];

        // Bounded so a misbehaving provider fails the test instead of hanging it.
        var limit = (int)(DurationMs / 1000.0 * SampleRate) * 4;

        while (samples.Count < limit)
        {
            var read = provider.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;

            for (var i = 0; i < read; i++)
            {
                samples.Add(buffer[i]);
            }
        }

        return samples;
    }

    /// <summary>A flat DC source, so any change in a read sample is the envelope and nothing else.</summary>
    private sealed class ConstantSampleProvider : ISampleProvider
    {
        private readonly float _value;

        public WaveFormat WaveFormat { get; }

        public ConstantSampleProvider(int sampleRate, float value)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
            _value = value;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++)
            {
                buffer[offset + i] = _value;
            }

            return count;
        }
    }
}
