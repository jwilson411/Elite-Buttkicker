using EDButtkicker.Models;
using EDButtkicker.Services;
using NAudio.Wave;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Verifies that the app-level hardware safety cap bounds every pattern type after mixing.
/// Everything here runs on plain sample providers - no WASAPI, no WaveOut, no real device.
/// </summary>
public class HardwareSafetyLimiterTests
{
    private const int SampleRate = 44100;
    private const int DurationMs = 200;
    private const float Tolerance = 1e-5f;

    public static TheoryData<int> MaxIntensities => new() { 0, 25, 80, 100 };

    [Theory]
    [MemberData(nameof(MaxIntensities))]
    public void Limiter_BoundsConstantAmplitudeSource(int maxIntensity)
    {
        // A source pinned to +/-1.0 - louder than every cap under test except 100.
        var source = new ConstantAmplitudeSampleProvider(SampleRate, TotalSamples());
        var limited = AudioSafety.Limit(source, maxIntensity);

        AssertBoundedByCap(ReadAll(limited), maxIntensity);
    }

    [Theory]
    [MemberData(nameof(MaxIntensities))]
    public void Create_BoundsStandardPattern(int maxIntensity)
    {
        var pattern = new HapticPattern
        {
            Name = "Test Sustained",
            Pattern = PatternType.SustainedRumble,
            Frequency = 40,
            Duration = DurationMs,
            Intensity = 100
        };

        var provider = HapticSampleFactory.Create(pattern, 100, 40, SampleRate, maxIntensity);

        AssertBoundedByCap(ReadAll(provider), maxIntensity);
    }

    [Theory]
    [MemberData(nameof(MaxIntensities))]
    public void Create_BoundsOscillatingPattern(int maxIntensity)
    {
        var pattern = new HapticPattern
        {
            Name = "Overheating Warning",
            Pattern = PatternType.Oscillating,
            Frequency = 40,
            Duration = DurationMs,
            Intensity = 100
        };

        var provider = HapticSampleFactory.Create(pattern, 100, 40, SampleRate, maxIntensity);

        AssertBoundedByCap(ReadAll(provider), maxIntensity);
    }

    [Theory]
    [MemberData(nameof(MaxIntensities))]
    public void Create_BoundsSequencePattern(int maxIntensity)
    {
        var provider = HapticSampleFactory.Create(BuildSequencePattern(), 100, 40, SampleRate, maxIntensity);

        AssertBoundedByCap(ReadAll(provider), maxIntensity);
    }

    [Theory]
    [MemberData(nameof(MaxIntensities))]
    public void Create_BoundsMultiLayerPattern(int maxIntensity)
    {
        var provider = HapticSampleFactory.Create(BuildMultiLayerPattern(), 100, 40, SampleRate, maxIntensity);

        AssertBoundedByCap(ReadAll(provider), maxIntensity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(80)]
    public void MultiLayerMixWouldExceedCapWithoutTheLimiter(int maxIntensity)
    {
        // Two in-phase layers at amplitude 1.0 mix well past the cap; only the limiter pulls it back.
        var unlimited = HapticSampleFactory.CreateMultiLayerPattern(BuildMultiLayerPattern(), SampleRate);

        var peak = ReadAll(unlimited).Max(Math.Abs);

        Assert.True(peak > maxIntensity / 100f + Tolerance,
            $"Expected the unlimited mix to exceed the {maxIntensity}% cap, but its peak was {peak}.");
    }

    /// <summary>Two overlapping full-amplitude layers, so the mixed peak exceeds the cap on its own.</summary>
    private static HapticPattern BuildMultiLayerPattern() => new()
    {
        Name = "Test MultiLayer",
        Pattern = PatternType.MultiLayer,
        Frequency = 40,
        Duration = DurationMs,
        Intensity = 100,
        Layers =
        {
            new PatternLayer { Waveform = WaveformType.Sine, Frequency = 40, Amplitude = 1.0f },
            new PatternLayer { Waveform = WaveformType.Sine, Frequency = 40, Amplitude = 1.0f }
        }
    };

    /// <summary>Staggered layer start times, with an overlapping window in the middle.</summary>
    private static HapticPattern BuildSequencePattern() => new()
    {
        Name = "Test Sequence",
        Pattern = PatternType.Sequence,
        Frequency = 40,
        Duration = DurationMs,
        Intensity = 100,
        Layers =
        {
            new PatternLayer { Waveform = WaveformType.Sine, Frequency = 40, Amplitude = 1.0f, StartTime = 0, Duration = 120 },
            new PatternLayer { Waveform = WaveformType.Sine, Frequency = 40, Amplitude = 1.0f, StartTime = 80, Duration = 120 }
        }
    };

    private static void AssertBoundedByCap(IReadOnlyList<float> samples, int maxIntensity)
    {
        Assert.NotEmpty(samples);

        var cap = maxIntensity / 100f + Tolerance;

        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];

            Assert.False(float.IsNaN(sample) || float.IsInfinity(sample),
                $"Sample {i} was not a finite value ({sample}).");

            if (maxIntensity == 0)
            {
                Assert.Equal(0f, sample);
                continue;
            }

            Assert.True(Math.Abs(sample) <= cap,
                $"Sample {i} was {sample}, above the {maxIntensity}% cap.");
        }
    }

    private static int TotalSamples() => (int)(DurationMs / 1000.0 * SampleRate);

    private static List<float> ReadAll(ISampleProvider provider)
    {
        var samples = new List<float>();
        var buffer = new float[1024];

        // Bounded so a misbehaving provider fails the test instead of hanging it.
        var limit = TotalSamples() * 4;

        while (samples.Count < limit)
        {
            var read = provider.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;

            for (int i = 0; i < read; i++)
            {
                samples.Add(buffer[i]);
            }
        }

        return samples;
    }

    /// <summary>Full-scale square source: the loudest thing the limiter can be handed.</summary>
    private sealed class ConstantAmplitudeSampleProvider : ISampleProvider
    {
        private readonly int _totalSamples;
        private int _position;

        public WaveFormat WaveFormat { get; }

        public ConstantAmplitudeSampleProvider(int sampleRate, int totalSamples)
        {
            _totalSamples = totalSamples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var toRead = Math.Min(count, _totalSamples - _position);
            if (toRead <= 0) return 0;

            for (int i = 0; i < toRead; i++)
            {
                buffer[offset + i] = (_position + i) % 2 == 0 ? 1.0f : -1.0f;
            }

            _position += toRead;
            return toRead;
        }
    }
}
