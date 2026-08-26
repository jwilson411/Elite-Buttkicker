using NAudio.Wave;

namespace EDButtkicker.Services;

/// <summary>
/// Final, app-wide output bound. Wraps any sample provider and clamps every sample to
/// ±(MaxIntensity / 100) so no pattern type can drive the transducer past the configured
/// Audio.MaxIntensity, regardless of how loud the upstream mix got.
/// </summary>
public class HardwareSafetyLimiter : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float _ceiling;

    /// <summary>Configured app maximum intensity (0-100) enforced by this limiter.</summary>
    public int MaxIntensity { get; }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public HardwareSafetyLimiter(ISampleProvider source, int maxIntensity)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        MaxIntensity = Math.Clamp(maxIntensity, 0, 100);
        _ceiling = MaxIntensity / 100f;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var samplesRead = _source.Read(buffer, offset, count);
        if (samplesRead <= 0) return samplesRead;

        // A zero ceiling means silence - never let rounding leave a residual signal.
        if (_ceiling <= 0f)
        {
            Array.Clear(buffer, offset, samplesRead);
            return samplesRead;
        }

        for (int i = 0; i < samplesRead; i++)
        {
            var sample = buffer[offset + i];

            // NaN/Infinity from an upstream envelope must not reach the hardware.
            if (float.IsNaN(sample))
            {
                buffer[offset + i] = 0f;
                continue;
            }

            if (sample > _ceiling) buffer[offset + i] = _ceiling;
            else if (sample < -_ceiling) buffer[offset + i] = -_ceiling;
        }

        return samplesRead;
    }
}

/// <summary>
/// Single entry point for applying the app-level hardware safety cap.
/// </summary>
public static class AudioSafety
{
    /// <summary>
    /// Bounds <paramref name="source"/> to the configured app maximum intensity (0-100).
    /// This is the last stage applied to every pattern, after all mixing and envelopes.
    /// </summary>
    public static ISampleProvider Limit(ISampleProvider source, int maxIntensity)
        => new HardwareSafetyLimiter(source, maxIntensity);
}
