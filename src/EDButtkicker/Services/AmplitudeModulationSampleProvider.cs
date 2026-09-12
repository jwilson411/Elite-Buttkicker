using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EDButtkicker.Services;

public class AmplitudeModulationSampleProvider : ISampleProvider
{
    /// <summary>
    /// Samples the modulation scratch buffer is sized to up front. This covers every buffer size the
    /// app can actually be driven with: the largest configurable audio buffer
    /// (<see cref="SettingsPersistenceService.MaxBufferSize"/>) and the ~9600 samples a 200ms
    /// shared-mode WASAPI output asks for at 48kHz. 64 KB of float, allocated once per provider.
    /// </summary>
    private const int ModulationBufferSamples = 16384;

    private readonly ISampleProvider _sourceProvider;
    private readonly SignalGenerator _modulationGenerator;
    private readonly float _modulationDepth;

    /// <summary>
    /// Preallocated so the real-time audio callback allocates nothing once playback is running.
    /// Not readonly only because of the grow path in <see cref="Read"/>.
    /// </summary>
    private float[] _modulationBuffer = new float[ModulationBufferSamples];

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
        
        // Fill the preallocated modulation buffer. A host that asks for more than the preallocated
        // size grows it exactly once and every later callback reuses the grown buffer, so this is a
        // one-time cost on a new maximum rather than a per-callback (steady-state) allocation.
        if (_modulationBuffer.Length < count)
        {
            _modulationBuffer = new float[count];
        }

        _modulationGenerator.Read(_modulationBuffer, 0, count);

        // Apply amplitude modulation
        for (int i = 0; i < samplesRead; i++)
        {
            var modulationValue = _modulationBuffer[i]; // -1 to 1
            var normalizedModulation = (modulationValue + 1.0f) * 0.5f; // 0 to 1
            var amplitudeMultiplier = 1.0f - _modulationDepth + (_modulationDepth * normalizedModulation);
            
            buffer[offset + i] *= amplitudeMultiplier;
        }
        
        return samplesRead;
    }
}