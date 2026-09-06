using NAudio.Wave;

namespace EDButtkicker.Services;

/// <summary>
/// Shared envelope timings. Every pattern starts at zero gain and ends at zero gain, and every
/// cancellation ramps to zero inside a bounded interval rather than cutting at full amplitude.
/// </summary>
public static class HapticEnvelope
{
    /// <summary>
    /// Bound for a cancelled or shut-down effect: gain reaches zero this many milliseconds after
    /// <see cref="ReleaseEnvelopeSampleProvider.BeginRelease"/>, so a stop is a ramp, not a pop.
    /// </summary>
    public const int ReleaseMilliseconds = 30;

    /// <summary>Extra time allowed for the output to consume the release ramp before inputs are removed.</summary>
    public const int ReleaseSettleMilliseconds = 10;

    /// <summary>Default click-prevention ramp when a pattern asks for no fade at all.</summary>
    public const double ClickGuardMilliseconds = 5.0;

    /// <summary>
    /// The shortest ramp that still avoids a click: the click guard, or one cycle of the carrier
    /// when the carrier is fast enough that a full cycle is shorter than the guard.
    /// </summary>
    public static double MinimumRampMilliseconds(int frequency)
    {
        if (frequency <= 0) return ClickGuardMilliseconds;

        return Math.Min(ClickGuardMilliseconds, 1000.0 / frequency);
    }

    /// <summary>Converts a millisecond timing to a whole number of samples at the given rate.</summary>
    public static int ToSamples(double milliseconds, int sampleRate)
    {
        if (milliseconds <= 0 || sampleRate <= 0) return 0;

        return (int)Math.Round(milliseconds / 1000.0 * sampleRate);
    }
}

/// <summary>
/// Sample-accurate attack/sustain/decay envelope. The gain for each sample is derived from its
/// index, the sample rate and the millisecond timings, so the shape does not depend on buffer sizes
/// or on any timer. The provider also owns the pattern duration: it never returns more than
/// <see cref="TotalSamples"/> samples, so the generated stream is exactly the pattern length and the
/// final sample is on the way down to zero.
/// </summary>
public sealed class EnvelopeSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _totalSamples;
    private readonly int _attackSamples;
    private readonly int _decaySamples;
    private readonly int _decayStart;
    private int _position;

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Length of the shaped stream in samples - the pattern duration at the source rate.</summary>
    public int TotalSamples => _totalSamples;

    /// <summary>Attack length in samples after clamping against the duration.</summary>
    public int AttackSamples => _attackSamples;

    /// <summary>Decay length in samples after clamping against the duration.</summary>
    public int DecaySamples => _decaySamples;

    public EnvelopeSampleProvider(ISampleProvider source, int durationMs, double attackMs, double decayMs)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));

        var sampleRate = source.WaveFormat.SampleRate;
        _totalSamples = Math.Max(1, HapticEnvelope.ToSamples(Math.Max(0, durationMs), sampleRate));

        var attack = Math.Max(0, HapticEnvelope.ToSamples(attackMs, sampleRate));
        var decay = Math.Max(0, HapticEnvelope.ToSamples(decayMs, sampleRate));

        // Attack and decay can be asked for independently (pattern shape, FadeIn, FadeOut) and can
        // together outlast the pattern. Scale them down proportionally rather than letting one eat
        // the other, so a short pattern still ramps up and back down inside its own duration.
        var requested = attack + decay;
        if (requested > _totalSamples && requested > 0)
        {
            var scale = _totalSamples / (double)requested;
            attack = (int)(attack * scale);
            decay = (int)(decay * scale);
        }

        _attackSamples = Math.Clamp(attack, 0, _totalSamples);
        _decaySamples = Math.Clamp(decay, 0, _totalSamples - _attackSamples);
        _decayStart = _totalSamples - _decaySamples;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var remaining = _totalSamples - _position;
        if (remaining <= 0) return 0;

        var samplesRead = _source.Read(buffer, offset, Math.Min(count, remaining));
        if (samplesRead <= 0) return samplesRead;

        for (int i = 0; i < samplesRead; i++)
        {
            buffer[offset + i] *= GainAt(_position + i);
        }

        _position += samplesRead;
        return samplesRead;
    }

    /// <summary>
    /// Gain for one sample index. Attack and decay are linear ramps and compose by taking the
    /// quieter of the two, so an overlap never gets louder than either ramp alone.
    /// </summary>
    public float GainAt(int index)
    {
        if (index < 0 || index >= _totalSamples) return 0f;

        var gain = 1f;

        if (_attackSamples > 0 && index < _attackSamples)
        {
            gain = (float)(index / (double)_attackSamples);
        }

        if (_decaySamples > 0 && index >= _decayStart)
        {
            gain = Math.Min(gain, (float)((_totalSamples - index) / (double)_decaySamples));
        }

        return gain;
    }
}

/// <summary>
/// Cancellation envelope. Passes audio through untouched until <see cref="BeginRelease"/>, then
/// ramps the gain linearly to zero within <see cref="ReleaseMilliseconds"/> and reports end of
/// stream. This is what wraps every mixer input, so stopping, disposing or reinitialising the audio
/// engine fades the transducer out instead of cutting it at whatever amplitude it happened to be at.
/// </summary>
public sealed class ReleaseEnvelopeSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _releaseSamples;
    private volatile bool _releasing;
    private volatile bool _releaseComplete;
    private int _releasePosition;

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Length of the release ramp in milliseconds - the bound a stop is guaranteed within.</summary>
    public int ReleaseMilliseconds { get; }

    /// <summary>Length of the release ramp in samples at the source rate.</summary>
    public int ReleaseSamples => _releaseSamples;

    /// <summary>True once <see cref="BeginRelease"/> has been called.</summary>
    public bool IsReleasing => _releasing;

    /// <summary>True once the ramp has reached zero and the provider has gone silent.</summary>
    public bool IsReleaseComplete => _releaseComplete;

    public ReleaseEnvelopeSampleProvider(ISampleProvider source, int releaseMilliseconds = HapticEnvelope.ReleaseMilliseconds)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        ReleaseMilliseconds = Math.Max(0, releaseMilliseconds);
        _releaseSamples = HapticEnvelope.ToSamples(ReleaseMilliseconds, source.WaveFormat.SampleRate);
    }

    /// <summary>
    /// Starts the ramp to zero. Safe to call more than once and from a thread other than the audio
    /// callback: the ramp position only advances inside <see cref="Read"/>.
    /// </summary>
    public void BeginRelease()
    {
        if (_releasing) return;

        _releasing = true;
        if (_releaseSamples <= 0) _releaseComplete = true;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // A completed release is end of stream: the mixer drops the input rather than looping silence.
        if (_releaseComplete) return 0;

        var samplesRead = _source.Read(buffer, offset, count);
        if (samplesRead <= 0) return samplesRead;

        if (!_releasing) return samplesRead;

        for (int i = 0; i < samplesRead; i++)
        {
            buffer[offset + i] *= GainAt(_releasePosition + i);
        }

        _releasePosition += samplesRead;
        if (_releasePosition >= _releaseSamples) _releaseComplete = true;

        return samplesRead;
    }

    /// <summary>Release gain at a position measured in samples from the start of the ramp.</summary>
    public float GainAt(int samplesSinceRelease)
    {
        if (_releaseSamples <= 0 || samplesSinceRelease >= _releaseSamples) return 0f;
        if (samplesSinceRelease <= 0) return 1f;

        return 1f - (float)(samplesSinceRelease / (double)_releaseSamples);
    }
}
