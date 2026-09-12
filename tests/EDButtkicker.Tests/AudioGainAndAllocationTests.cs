using EDButtkicker.Models;
using EDButtkicker.Services;
using NAudio.Wave;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Two properties of the generation path that only sample-level assertions can catch: each layer's
/// Amplitude is applied exactly once (it used to be applied in both the waveform generator and the
/// mixing step, which squared it - an intended 0.5 rendered at 0.25), and a steady-state Read on the
/// real-time audio callback thread allocates nothing. Sample providers only - no WASAPI, no WaveOut,
/// no real device, so nothing here says anything about what the hardware does.
/// </summary>
public class AudioGainAndAllocationTests
{
    private const int SampleRate = 44100;
    private const int DurationMs = 200;

    /// <summary>One mixer buffer, matching what the rest of the suite reads with.</summary>
    private const int BufferSamples = 1024;

    /// <summary>Long enough that the allocation loop below never runs off the end of the pattern.</summary>
    private const int LongDurationMs = 60_000;

    /// <summary>Reads per allocation loop: 2000 x 1024 samples is ~46s of audio at 44.1kHz.</summary>
    private const int SteadyStateReads = 2000;

    [Theory]
    [InlineData(1.0f)]
    [InlineData(0.8f)]
    [InlineData(0.5f)]
    [InlineData(0.25f)]
    public void SingleLayer_PeakIsTheLayerAmplitudeNotItsSquare(float amplitude)
    {
        // A square wave sits at exactly +/-1, and the Sine curve reaches exactly 1.0 at the midpoint
        // of the layer, so the pattern's peak is the layer amplitude and nothing else.
        var samples = ReadAll(new MultiLayerPatternGenerator(BuildSingleLayerPattern(WaveformType.Square, amplitude), SampleRate));

        Assert.NotEmpty(samples);
        Assert.Equal(amplitude, Peak(samples), 5);

        // The old double-application would land here instead (1.0 is the one amplitude it cannot
        // distinguish, since squaring it is a no-op).
        if (amplitude < 1.0f)
        {
            Assert.NotEqual(amplitude * amplitude, Peak(samples), 5);
        }
    }

    [Fact]
    public void SingleLayer_SineWaveformMatchesGoldenSampleValues()
    {
        // Known frequency/amplitude/duration -> closed-form expected samples:
        //   amplitude * sin(2*pi*f*t) * sin(pi * progress)
        // which is the documented gain order with every stage applied once.
        const float Amplitude = 0.5f;
        const int Frequency = 40;

        var samples = ReadAll(new MultiLayerPatternGenerator(BuildSingleLayerPattern(WaveformType.Sine, Amplitude, Frequency), SampleRate));

        Assert.Equal((int)(DurationMs / 1000.0 * SampleRate), samples.Count);

        for (int i = 0; i < samples.Count; i++)
        {
            double time = (double)i / SampleRate;
            double progress = (time * 1000.0) / DurationMs;
            double expected = Amplitude * Math.Sin(2 * Math.PI * Frequency * time) * Math.Sin(Math.PI * progress);

            Assert.True(Math.Abs(samples[i] - expected) < 1e-5,
                $"Sample {i} was {samples[i]}, expected {expected} (gain applied more or fewer than once?).");
        }
    }

    [Fact]
    public void SingleLayer_HalfAmplitudeIsExactlyHalfOfFullAmplitude()
    {
        // Independent of any closed form: halving the layer amplitude has to halve every sample.
        var full = ReadAll(new MultiLayerPatternGenerator(BuildSingleLayerPattern(WaveformType.Sawtooth, 1.0f), SampleRate));
        var half = ReadAll(new MultiLayerPatternGenerator(BuildSingleLayerPattern(WaveformType.Sawtooth, 0.5f), SampleRate));

        Assert.Equal(full.Count, half.Count);
        Assert.Contains(full, s => s != 0f);

        for (int i = 0; i < full.Count; i++)
        {
            Assert.True(Math.Abs(half[i] - (full[i] * 0.5f)) < 1e-6f,
                $"Sample {i}: half-amplitude layer gave {half[i]}, expected {full[i] * 0.5f}.");
        }
    }

    [Fact]
    public void MultiLayerGenerator_SteadyStateReadAllocatesNothing()
    {
        var generator = new MultiLayerPatternGenerator(BuildLongMultiLayerPattern(), SampleRate);
        var buffer = new float[BufferSamples];

        // Warmup: the first call JITs the read path, so its allocations are not steady state.
        Assert.Equal(BufferSamples, generator.Read(buffer, 0, BufferSamples));

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < SteadyStateReads; i++)
        {
            generator.Read(buffer, 0, BufferSamples);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void MultiLayerGenerator_OversizedReadGrowsOnceAndThenStopsAllocating()
    {
        // Larger than the preallocated per-layer buffer: the first such call is allowed to grow,
        // every call after it has to reuse the grown buffer.
        const int Oversized = 20_000;

        var generator = new MultiLayerPatternGenerator(BuildLongMultiLayerPattern(), SampleRate);
        var buffer = new float[Oversized];

        generator.Read(buffer, 0, BufferSamples); // warmup at the normal size
        generator.Read(buffer, 0, Oversized);     // the one-time grow

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            generator.Read(buffer, 0, Oversized);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void AmplitudeModulation_SteadyStateReadAllocatesNothing()
    {
        var modulated = new AmplitudeModulationSampleProvider(
            HapticSampleFactory.CreateSignalGenerator(80, 40, SampleRate), 2.0, 0.5f);
        var buffer = new float[BufferSamples];

        // Warmup: the first call JITs the read path, so its allocations are not steady state.
        Assert.Equal(BufferSamples, modulated.Read(buffer, 0, BufferSamples));

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < SteadyStateReads; i++)
        {
            modulated.Read(buffer, 0, BufferSamples);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void AmplitudeModulation_OversizedReadGrowsOnceAndThenStopsAllocating()
    {
        const int Oversized = 20_000;

        var modulated = new AmplitudeModulationSampleProvider(
            HapticSampleFactory.CreateSignalGenerator(80, 40, SampleRate), 2.0, 0.5f);
        var buffer = new float[Oversized];

        modulated.Read(buffer, 0, BufferSamples); // warmup at the normal size
        modulated.Read(buffer, 0, Oversized);     // the one-time grow

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            modulated.Read(buffer, 0, Oversized);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void AmplitudeModulation_StillModulatesAcrossTheReusedBuffer()
    {
        // The reused scratch buffer must not stall the modulator: depth 1.0 means the signal has to
        // reach both near-silence and near-full-scale over a full modulation cycle.
        var modulated = new AmplitudeModulationSampleProvider(
            HapticSampleFactory.CreateSignalGenerator(100, 40, SampleRate), 2.0, 1.0f);

        var buffer = new float[BufferSamples];
        var envelope = new List<float>();

        // 2Hz modulation over 0.5s: one full cycle, read in mixer-sized chunks.
        for (int chunk = 0; chunk < SampleRate / 2 / BufferSamples; chunk++)
        {
            var read = modulated.Read(buffer, 0, BufferSamples);
            var peak = 0f;
            for (int i = 0; i < read; i++)
            {
                peak = Math.Max(peak, Math.Abs(buffer[i]));
            }

            envelope.Add(peak);
        }

        Assert.Contains(envelope, p => p < 0.15f);
        Assert.Contains(envelope, p => p > 0.85f);
    }

    /// <summary>
    /// One layer, no fades, Sine curve so the layer's gain reaches exactly 1.0 at its midpoint -
    /// which makes the pattern's peak the layer amplitude and nothing else.
    /// </summary>
    private static HapticPattern BuildSingleLayerPattern(WaveformType waveform, float amplitude, int frequency = 20) => new()
    {
        Name = "Gain Staging Test",
        Pattern = PatternType.MultiLayer,
        Frequency = frequency,
        Duration = DurationMs,
        Intensity = 100,
        MaxIntensity = 100,
        Layers =
        {
            new PatternLayer
            {
                Waveform = waveform,
                Frequency = frequency,
                Amplitude = amplitude,
                Curve = IntensityCurve.Sine
            }
        }
    };

    private static HapticPattern BuildLongMultiLayerPattern() => new()
    {
        Name = "Allocation Test",
        Pattern = PatternType.MultiLayer,
        Frequency = 40,
        Duration = LongDurationMs,
        Intensity = 100,
        MaxIntensity = 100,
        Layers =
        {
            new PatternLayer { Waveform = WaveformType.Sine, Frequency = 18, Amplitude = 0.8f, FadeIn = 50, FadeOut = 50 },
            new PatternLayer { Waveform = WaveformType.Square, Frequency = 40, Amplitude = 0.5f, PhaseOffset = 90 },
            new PatternLayer { Waveform = WaveformType.Noise, Frequency = 72, Amplitude = 0.2f, Curve = IntensityCurve.Bounce }
        }
    };

    private static float Peak(IReadOnlyList<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
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

            for (int i = 0; i < read; i++)
            {
                samples.Add(buffer[i]);
            }
        }

        return samples;
    }
}
