using NAudio.Wave;
using EDButtkicker.Models;

namespace EDButtkicker.Services;

public class MultiLayerPatternGenerator : ISampleProvider
{
    private readonly List<LayerGenerator> _layers = new();
    private readonly HapticPattern _pattern;
    private readonly int _sampleRate;
    private long _samplePosition;
    private readonly int _totalSamples;

    public WaveFormat WaveFormat { get; }

    private class LayerGenerator
    {
        public AdvancedWaveformGenerator Generator { get; set; }
        public PatternLayer Layer { get; set; }
        public float[] Buffer { get; set; }

        public LayerGenerator(AdvancedWaveformGenerator generator, PatternLayer layer)
        {
            Generator = generator;
            Layer = layer;
            Buffer = new float[1024]; // Buffer for mixing
        }
    }

    public MultiLayerPatternGenerator(HapticPattern pattern, int sampleRate, int channels = 1)
    {
        _pattern = pattern;
        _sampleRate = sampleRate;
        _totalSamples = (int)((pattern.Duration / 1000.0) * sampleRate);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        // Create base layer if no layers defined
        if (!pattern.Layers.Any())
        {
            var baseLayer = new PatternLayer
            {
                Waveform = pattern.Waveform,
                Frequency = pattern.Frequency,
                Amplitude = 1.0f,
                PhaseOffset = 0,
                Curve = pattern.IntensityCurve
            };
            pattern.Layers.Add(baseLayer);
        }

        // Initialize layer generators
        foreach (var layer in pattern.Layers)
        {
            var generator = new AdvancedWaveformGenerator(
                sampleRate, 
                channels, 
                layer.Waveform, 
                layer.Frequency, 
                layer.Amplitude, 
                layer.PhaseOffset
            );
            
            _layers.Add(new LayerGenerator(generator, layer));
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // Clear buffer
        Array.Clear(buffer, offset, count);
        
        int samplesToRead = Math.Min(count, _totalSamples - (int)_samplePosition);
        if (samplesToRead <= 0) return 0;

        // Current time in milliseconds
        double currentTimeMs = (_samplePosition * 1000.0) / _sampleRate;

        // Mix all layers that should be active at current time
        foreach (var layerGen in _layers)
        {
            var layer = layerGen.Layer;
            
            // Calculate layer timing
            int layerStartTime = layer.StartTime;
            int layerDuration = layer.Duration > 0 ? layer.Duration : _pattern.Duration;
            int layerEndTime = layerStartTime + layerDuration;
            
            // Skip layer if it's not active during this time window
            double endTimeMs = currentTimeMs + (samplesToRead * 1000.0 / _sampleRate);
            if (currentTimeMs >= layerEndTime || endTimeMs <= layerStartTime)
                continue;

            // Ensure buffer is large enough
            if (layerGen.Buffer.Length < samplesToRead)
            {
                layerGen.Buffer = new float[samplesToRead];
            }

            // Generate samples for this layer
            layerGen.Generator.Read(layerGen.Buffer, 0, samplesToRead);

            // Apply timing, fading, and intensity curve to each sample
            for (int i = 0; i < samplesToRead; i++)
            {
                double sampleTimeMs = currentTimeMs + (i * 1000.0 / _sampleRate);
                
                // Skip samples outside layer timing
                if (sampleTimeMs < layerStartTime || sampleTimeMs >= layerEndTime)
                    continue;

                // Calculate fade multipliers
                float fadeMultiplier = 1.0f;
                
                // Fade in
                if (layer.FadeIn > 0 && sampleTimeMs < layerStartTime + layer.FadeIn)
                {
                    fadeMultiplier *= (float)((sampleTimeMs - layerStartTime) / layer.FadeIn);
                }
                
                // Fade out
                if (layer.FadeOut > 0 && sampleTimeMs > layerEndTime - layer.FadeOut)
                {
                    fadeMultiplier *= (float)((layerEndTime - sampleTimeMs) / layer.FadeOut);
                }

                // Calculate layer progress for intensity curve
                float layerProgress = (float)((sampleTimeMs - layerStartTime) / layerDuration);
                layerProgress = Math.Max(0f, Math.Min(1f, layerProgress));
                
                float intensityMultiplier = IntensityCurveProcessor.CalculateIntensity(
                    layer.Curve, 
                    layerProgress, 
                    _pattern.Intensity / 100.0f,
                    _pattern.CustomCurvePoints
                );

                // Mix the layer into the main buffer
                buffer[offset + i] += layerGen.Buffer[i] * intensityMultiplier * layer.Amplitude * fadeMultiplier;
            }
        }

        // Apply overall intensity scaling and prevent clipping
        float maxIntensity = _pattern.MaxIntensity / 100.0f;
        for (int i = 0; i < samplesToRead; i++)
        {
            buffer[offset + i] = Math.Max(-maxIntensity, Math.Min(maxIntensity, buffer[offset + i]));
        }

        _samplePosition += samplesToRead;
        return samplesToRead;
    }
}

public class CurveEnvelopeSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _sourceProvider;
    private readonly IntensityCurve _curve;
    private readonly List<CurvePoint> _customPoints;
    private readonly int _totalSamples;
    private readonly float _baseIntensity;
    private long _samplePosition;

    public WaveFormat WaveFormat => _sourceProvider.WaveFormat;

    public CurveEnvelopeSampleProvider(ISampleProvider sourceProvider, IntensityCurve curve, int durationMs, float baseIntensity = 1.0f, List<CurvePoint>? customPoints = null)
    {
        _sourceProvider = sourceProvider;
        _curve = curve;
        _customPoints = customPoints ?? new List<CurvePoint>();
        _totalSamples = (int)((durationMs / 1000.0) * sourceProvider.WaveFormat.SampleRate);
        _baseIntensity = baseIntensity;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var samplesRead = _sourceProvider.Read(buffer, offset, count);

        for (int i = 0; i < samplesRead; i++)
        {
            float progress = (float)(_samplePosition + i) / _totalSamples;
            float intensity = IntensityCurveProcessor.CalculateIntensity(_curve, progress, _baseIntensity, _customPoints);
            buffer[offset + i] *= intensity;
        }

        _samplePosition += samplesRead;
        return samplesRead;
    }
}