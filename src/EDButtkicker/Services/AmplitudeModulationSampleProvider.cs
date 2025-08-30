using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EDButtkicker.Services;

public class AmplitudeModulationSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _sourceProvider;
    private readonly SignalGenerator _modulationGenerator;
    private readonly float _modulationDepth;
    
    public WaveFormat WaveFormat => _sourceProvider.WaveFormat;

    public AmplitudeModulationSampleProvider(ISampleProvider sourceProvider, double modulationFrequency, float modulationDepth = 0.5f)
    {
        _sourceProvider = sourceProvider;
        _modulationDepth = Math.Min(1.0f, Math.Max(0.0f, modulationDepth)); // Clamp between 0 and 1
        
        _modulationGenerator = new SignalGenerator(sourceProvider.WaveFormat.SampleRate, sourceProvider.WaveFormat.Channels)
        {
            Frequency = modulationFrequency,
            Type = SignalGeneratorType.Sin,
            Gain = 1.0 // Full range for modulation
        };
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var samplesRead = _sourceProvider.Read(buffer, offset, count);
        
        // Create modulation buffer
        var modulationBuffer = new float[count];
        _modulationGenerator.Read(modulationBuffer, 0, count);
        
        // Apply amplitude modulation
        for (int i = 0; i < samplesRead; i++)
        {
            var modulationValue = modulationBuffer[i]; // -1 to 1
            var normalizedModulation = (modulationValue + 1.0f) * 0.5f; // 0 to 1
            var amplitudeMultiplier = 1.0f - _modulationDepth + (_modulationDepth * normalizedModulation);
            
            buffer[offset + i] *= amplitudeMultiplier;
        }
        
        return samplesRead;
    }
}