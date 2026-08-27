using EDButtkicker.Models;
using EDButtkicker.Services;
using NAudio.Wave;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Proves the advanced pattern fields (layers, waveform, curves, custom points) actually reach the
/// sample generation path, and that generation never writes back into the pattern it was handed.
/// Sample providers only - no WASAPI, no WaveOut, no real device.
/// </summary>
public class AdvancedPatternPlaybackTests
{
    private const int SampleRate = 44100;
    private const int DurationMs = 200;

    [Fact]
    public void MultiLayer_UsesTheDefinedLayersInsteadOfTheSynthesizedDefault()
    {
        var withLayers = BuildMultiLayerPattern();
        var control = BuildMultiLayerPattern();
        control.Layers.Clear(); // forces the synthesized sine-at-pattern.Frequency base layer

        var layered = ReadAll(HapticSampleFactory.CreateMultiLayerPattern(withLayers, SampleRate));
        var synthesized = ReadAll(HapticSampleFactory.CreateMultiLayerPattern(control, SampleRate));

        Assert.NotEmpty(layered);
        Assert.Equal(synthesized.Count, layered.Count);
        Assert.Contains(layered, s => s != 0f);
        Assert.True(layered.Where((s, i) => Math.Abs(s - synthesized[i]) > 1e-4f).Any(),
            "The defined layers produced the same signal as the synthesized default base layer.");
    }

    [Fact]
    public void MultiLayer_LayerFrequenciesChangeTheGeneratedSignal()
    {
        var baseline = BuildMultiLayerPattern();
        var retuned = BuildMultiLayerPattern();
        retuned.Layers[0].Frequency = 55;
        retuned.Layers[1].Frequency = 25;

        var baselineSamples = ReadAll(HapticSampleFactory.CreateMultiLayerPattern(baseline, SampleRate));
        var retunedSamples = ReadAll(HapticSampleFactory.CreateMultiLayerPattern(retuned, SampleRate));

        Assert.True(baselineSamples.Where((s, i) => Math.Abs(s - retunedSamples[i]) > 1e-4f).Any(),
            "Changing the layer frequencies made no difference to the generated samples.");
    }

    [Fact]
    public void MultiLayerGenerator_DoesNotMutateTheIncomingPattern()
    {
        var pattern = BuildMultiLayerPattern();
        var layerList = pattern.Layers;
        var firstLayer = pattern.Layers[0];

        _ = new MultiLayerPatternGenerator(pattern, SampleRate);

        Assert.Same(layerList, pattern.Layers);
        Assert.Same(firstLayer, pattern.Layers[0]);
        Assert.Equal(2, pattern.Layers.Count);
        Assert.Equal(WaveformType.Square, pattern.Layers[0].Waveform);
        Assert.Equal(18, pattern.Layers[0].Frequency);
    }

    [Fact]
    public void MultiLayerGenerator_WithNoLayers_SynthesizesLocallyAndLeavesTheListEmpty()
    {
        var pattern = BuildMultiLayerPattern();
        pattern.Layers.Clear();
        pattern.Waveform = WaveformType.Square;

        var samples = ReadAll(HapticSampleFactory.CreateMultiLayerPattern(pattern, SampleRate));

        Assert.Empty(pattern.Layers);
        Assert.NotEmpty(samples);
        Assert.Contains(samples, s => s != 0f);
    }

    [Fact]
    public void MultiLayerGenerator_WithNoLayers_UsesThePatternWaveformAndFrequency()
    {
        // Two no-layer patterns that differ only in the fields the synthesized base layer reads.
        var sine = BuildMultiLayerPattern();
        sine.Layers.Clear();
        sine.Waveform = WaveformType.Sine;
        sine.Frequency = 40;

        var square = BuildMultiLayerPattern();
        square.Layers.Clear();
        square.Waveform = WaveformType.Square;
        square.Frequency = 70;

        var sineSamples = ReadAll(HapticSampleFactory.CreateMultiLayerPattern(sine, SampleRate));
        var squareSamples = ReadAll(HapticSampleFactory.CreateMultiLayerPattern(square, SampleRate));

        Assert.True(sineSamples.Where((s, i) => Math.Abs(s - squareSamples[i]) > 1e-4f).Any(),
            "The synthesized base layer ignored the pattern waveform/frequency.");
    }

    [Fact]
    public void Create_MultiLayerPatternKeepsItsLayersThroughTheFactory()
    {
        var pattern = BuildMultiLayerPattern();

        var samples = ReadAll(HapticSampleFactory.Create(pattern, pattern.Intensity, pattern.Frequency, SampleRate, 100));

        Assert.Equal(2, pattern.Layers.Count);
        Assert.NotEmpty(samples);
        Assert.Contains(samples, s => s != 0f);
    }

    [Fact]
    public void Create_StandardPatternAppliesCustomCurvePoints()
    {
        // Points ramp the envelope down over the pattern, so the head is louder than the tail.
        var descending = BuildStandardCustomCurvePattern(new()
        {
            new CurvePoint { Time = 0.0f, Intensity = 1.0f },
            new CurvePoint { Time = 1.0f, Intensity = 0.0f }
        });

        var samples = ReadAll(HapticSampleFactory.Create(descending, descending.Intensity, descending.Frequency, SampleRate, 100));

        Assert.NotEmpty(samples);
        Assert.True(PeakOfFirstQuarter(samples) > PeakOfLastQuarter(samples),
            "Custom curve points did not shape the standard pattern envelope.");
    }

    [Fact]
    public void Create_StandardPatternWithoutCustomPointsFallsBackToTheLinearRamp()
    {
        // Same Custom curve, no points: the processor falls back to linear, so the tail is louder.
        var noPoints = BuildStandardCustomCurvePattern(new());

        var samples = ReadAll(HapticSampleFactory.Create(noPoints, noPoints.Intensity, noPoints.Frequency, SampleRate, 100));

        Assert.NotEmpty(samples);
        Assert.True(PeakOfLastQuarter(samples) > PeakOfFirstQuarter(samples),
            "Expected the point-less Custom curve to behave like the linear ramp.");
    }

    [Fact]
    public void Create_UsesTheClonedPatternsAdvancedFieldsEndToEnd()
    {
        // The full path a journal event takes: stored mapping -> clone + modifiers -> sample factory.
        var stored = HapticPatternCloneTests.BuildFullyPopulatedPattern();
        stored.Duration = DurationMs;
        var storedLayerCount = stored.Layers.Count;

        var played = EventPatternFactory.CreatePatternForEvent(stored, new JournalEvent { Event = "UnderAttack" });
        var samples = ReadAll(HapticSampleFactory.Create(played, played.Intensity, played.Frequency, SampleRate, 100));

        Assert.Equal(storedLayerCount, played.Layers.Count);
        Assert.Equal(storedLayerCount, stored.Layers.Count);
        Assert.NotEmpty(samples);
        Assert.Contains(samples, s => s != 0f);
    }

    private static HapticPattern BuildMultiLayerPattern() => new()
    {
        Name = "Layered Test",
        Pattern = PatternType.MultiLayer,
        Frequency = 40,
        Duration = DurationMs,
        Intensity = 100,
        MaxIntensity = 100,
        Waveform = WaveformType.Sine,
        Layers =
        {
            new PatternLayer { Waveform = WaveformType.Square, Frequency = 18, Amplitude = 0.8f },
            new PatternLayer { Waveform = WaveformType.Sawtooth, Frequency = 72, Amplitude = 0.5f, PhaseOffset = 90 }
        }
    };

    private static HapticPattern BuildStandardCustomCurvePattern(List<CurvePoint> points) => new()
    {
        Name = "Custom Curve Test",
        Pattern = PatternType.SustainedRumble,
        Frequency = 40,
        Duration = DurationMs,
        Intensity = 100,
        MaxIntensity = 100,
        IntensityCurve = IntensityCurve.Custom,
        CustomCurvePoints = points
    };

    private static float PeakOfFirstQuarter(IReadOnlyList<float> samples) =>
        PeakOfRange(samples, 0, samples.Count / 4);

    private static float PeakOfLastQuarter(IReadOnlyList<float> samples) =>
        PeakOfRange(samples, samples.Count - samples.Count / 4, samples.Count);

    private static float PeakOfRange(IReadOnlyList<float> samples, int start, int end)
    {
        var peak = 0f;
        for (int i = start; i < end; i++)
        {
            peak = Math.Max(peak, Math.Abs(samples[i]));
        }

        return peak;
    }

    private static List<float> ReadAll(ISampleProvider provider)
    {
        var samples = new List<float>();
        var buffer = new float[1024];

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
