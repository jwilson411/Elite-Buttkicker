using NAudio.Wave;
using EDButtkicker.Models;

namespace EDButtkicker.Services;

public class AdvancedWaveformGenerator : ISampleProvider
{
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly WaveformType _waveformType;
    private readonly double _frequency;
    private readonly float _amplitude;
    private readonly int _phaseOffset;
    private long _samplePosition;
    private readonly Random _random = new();

    public WaveFormat WaveFormat { get; }

    public AdvancedWaveformGenerator(int sampleRate, int channels, WaveformType waveformType, double frequency, float amplitude = 1.0f, int phaseOffset = 0)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _waveformType = waveformType;
        _frequency = frequency;
        _amplitude = Math.Max(0.0f, Math.Min(1.0f, amplitude));
        _phaseOffset = phaseOffset;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            double time = (double)_samplePosition / _sampleRate;
            double phase = 2 * Math.PI * _frequency * time + (_phaseOffset * Math.PI / 180.0);
            
            float sample = _waveformType switch
            {
                WaveformType.Sine => GenerateSine(phase),
                WaveformType.Square => GenerateSquare(phase),
                WaveformType.Triangle => GenerateTriangle(phase),
                WaveformType.Sawtooth => GenerateSawtooth(phase),
                WaveformType.Noise => GenerateNoise(),
                _ => GenerateSine(phase)
            };
            
            buffer[offset + i] = sample * _amplitude;
            _samplePosition++;
            
            // For multi-channel, duplicate to all channels
            if (_channels > 1 && i % _channels == 0)
            {
                for (int ch = 1; ch < _channels && i + ch < count; ch++)
                {
                    buffer[offset + i + ch] = buffer[offset + i];
                }
                i += _channels - 1;
            }
        }
        
        return count;
    }

    private float GenerateSine(double phase)
    {
        return (float)Math.Sin(phase);
    }

    private float GenerateSquare(double phase)
    {
        return Math.Sin(phase) >= 0 ? 1.0f : -1.0f;
    }

    private float GenerateTriangle(double phase)
    {
        double normalizedPhase = (phase % (2 * Math.PI)) / (2 * Math.PI);
        if (normalizedPhase < 0.5)
            return (float)(4 * normalizedPhase - 1);
        else
            return (float)(3 - 4 * normalizedPhase);
    }

    private float GenerateSawtooth(double phase)
    {
        double normalizedPhase = (phase % (2 * Math.PI)) / (2 * Math.PI);
        return (float)(2 * normalizedPhase - 1);
    }

    private float GenerateNoise()
    {
        return (float)(_random.NextDouble() * 2 - 1);
    }
}