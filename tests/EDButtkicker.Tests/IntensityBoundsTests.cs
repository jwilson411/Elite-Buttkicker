using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Intensity must stay inside its declared range at every stage: the curve processor never returns
/// a value outside 0..1, and a pattern whose per-event modifiers push it past 100% still reaches the
/// output bounded by the configured app maximum. Pure sample math - no device is opened.
/// </summary>
public class IntensityBoundsTests
{
    private const int SampleRate = 44100;
    private const float Tolerance = 1e-5f;

    public static TheoryData<IntensityCurve> AllCurves => new()
    {
        IntensityCurve.Linear,
        IntensityCurve.Exponential,
        IntensityCurve.Logarithmic,
        IntensityCurve.Sine,
        IntensityCurve.Bounce,
        IntensityCurve.Custom
    };

    [Theory]
    [MemberData(nameof(AllCurves))]
    public void CalculateIntensity_StaysWithinZeroToOneAcrossTheWholeCurve(IntensityCurve curve)
    {
        for (var step = 0; step <= 200; step++)
        {
            var time = step / 200f;
            var intensity = IntensityCurveProcessor.CalculateIntensity(curve, time, 1.0f);

            Assert.InRange(intensity, 0f, 1f);
        }
    }

    [Theory]
    [MemberData(nameof(AllCurves))]
    public void CalculateIntensity_ClampsTimeOutsideZeroToOne(IntensityCurve curve)
    {
        var below = IntensityCurveProcessor.CalculateIntensity(curve, -5f, 1.0f);
        var above = IntensityCurveProcessor.CalculateIntensity(curve, 5f, 1.0f);

        Assert.InRange(below, 0f, 1f);
        Assert.InRange(above, 0f, 1f);
        Assert.Equal(IntensityCurveProcessor.CalculateIntensity(curve, 0f, 1.0f), below, Tolerance);
        Assert.Equal(IntensityCurveProcessor.CalculateIntensity(curve, 1f, 1.0f), above, Tolerance);
    }

    [Theory]
    [MemberData(nameof(AllCurves))]
    public void CalculateIntensity_ClampsABaseIntensityAboveOne(IntensityCurve curve)
    {
        for (var step = 0; step <= 20; step++)
        {
            var intensity = IntensityCurveProcessor.CalculateIntensity(curve, step / 20f, 10.0f);

            Assert.InRange(intensity, 0f, 1f);
        }
    }

    [Theory]
    [MemberData(nameof(AllCurves))]
    public void CalculateIntensity_WithNoBaseIntensity_IsSilent(IntensityCurve curve)
    {
        Assert.Equal(0f, IntensityCurveProcessor.CalculateIntensity(curve, 0.5f, 0f), Tolerance);
    }

    [Fact]
    public void BounceCurve_OvershootIsClampedRatherThanLeakingThrough()
    {
        // The bounce easing overshoots 1.0 near the end; the processor must bound it.
        var values = Enumerable.Range(0, 101)
            .Select(i => IntensityCurveProcessor.CalculateIntensity(IntensityCurve.Bounce, i / 100f, 1.0f))
            .ToList();

        Assert.All(values, v => Assert.InRange(v, 0f, 1f));
        Assert.Contains(values, v => v >= 1f - Tolerance);
    }

    [Fact]
    public void LinearCurve_TracksTimeExactly()
    {
        Assert.Equal(0f, IntensityCurveProcessor.CalculateIntensity(IntensityCurve.Linear, 0f, 1f), Tolerance);
        Assert.Equal(0.25f, IntensityCurveProcessor.CalculateIntensity(IntensityCurve.Linear, 0.5f, 0.5f), Tolerance);
        Assert.Equal(1f, IntensityCurveProcessor.CalculateIntensity(IntensityCurve.Linear, 1f, 1f), Tolerance);
    }

    [Fact]
    public void CustomCurve_InterpolatesBetweenPointsAndStaysBounded()
    {
        var points = new List<CurvePoint>
        {
            new() { Time = 0.0f, Intensity = 0.0f },
            new() { Time = 0.5f, Intensity = 1.0f },
            new() { Time = 1.0f, Intensity = 0.0f }
        };

        Assert.Equal(0.5f, IntensityCurveProcessor.CalculateIntensity(IntensityCurve.Custom, 0.25f, 1f, points), Tolerance);
        Assert.Equal(1.0f, IntensityCurveProcessor.CalculateIntensity(IntensityCurve.Custom, 0.5f, 1f, points), Tolerance);

        for (var step = 0; step <= 100; step++)
        {
            Assert.InRange(IntensityCurveProcessor.CalculateIntensity(IntensityCurve.Custom, step / 100f, 1f, points), 0f, 1f);
        }
    }

    [Fact]
    public void CustomCurve_WithOutOfRangePoints_IsStillBounded()
    {
        var points = new List<CurvePoint>
        {
            new() { Time = 0.0f, Intensity = -3.0f },
            new() { Time = 0.5f, Intensity = 8.0f },
            new() { Time = 1.0f, Intensity = 4.0f }
        };

        for (var step = 0; step <= 100; step++)
        {
            Assert.InRange(IntensityCurveProcessor.CalculateIntensity(IntensityCurve.Custom, step / 100f, 1f, points), 0f, 1f);
        }
    }

    [Fact]
    public void CustomCurve_WithNoPoints_FallsBackToLinear()
    {
        var custom = IntensityCurveProcessor.CalculateIntensity(IntensityCurve.Custom, 0.4f, 1f, new List<CurvePoint>());
        var linear = IntensityCurveProcessor.CalculateIntensity(IntensityCurve.Linear, 0.4f, 1f);

        Assert.Equal(linear, custom, Tolerance);
    }

    [Theory]
    [MemberData(nameof(AllCurves))]
    public void CurvePreview_IsFullyBounded(IntensityCurve curve)
    {
        var preview = IntensityCurveProcessor.GenerateCurvePreview(curve);

        Assert.Equal(100, preview.Count);
        Assert.All(preview, value => Assert.InRange(value, 0f, 1f));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(80)]
    [InlineData(100)]
    public void EventBoostedPattern_IsStillBoundedByTheAppMaximum(int appMaxIntensity)
    {
        // "Interdicted" adds 20 on top of an already maximal pattern, so the per-event copy asks for
        // more than 100%. The app-level cap is what has to hold.
        var stored = new HapticPattern
        {
            Name = "Interdiction",
            Pattern = PatternType.BuildupRumble,
            Frequency = 40,
            Duration = 200,
            Intensity = 100,
            MaxIntensity = 100
        };

        var journalEvent = new JournalEvent { Event = "Interdicted" };
        var perEvent = EventPatternFactory.CreatePatternForEvent(stored, journalEvent, NullLogger.Instance);

        Assert.Equal(100, stored.Intensity); // the stored mapping keeps its own value
        var provider = HapticSampleFactory.Create(
            perEvent, perEvent.Intensity, perEvent.Frequency, SampleRate, appMaxIntensity);

        var ceiling = appMaxIntensity / 100f;
        foreach (var sample in ReadAll(provider))
        {
            Assert.False(float.IsNaN(sample));
            Assert.InRange(sample, -ceiling - Tolerance, ceiling + Tolerance);
        }
    }

    [Fact]
    public void HullDamagePattern_ScaledByHealth_StaysWithinItsDeclaredIntensityRange()
    {
        var stored = new HapticPattern
        {
            Name = "Hull Damage",
            Pattern = PatternType.SharpPulse,
            Frequency = 50,
            Duration = 200,
            Intensity = 80,
            IntensityFromDamage = true,
            MinIntensity = 30,
            MaxIntensity = 100
        };

        foreach (var health in new[] { 0.0, 0.25, 0.5, 0.99, 1.0 })
        {
            var perEvent = EventPatternFactory.CreatePatternForEvent(
                stored, new JournalEvent { Event = "HullDamage", Health = health }, NullLogger.Instance);

            Assert.InRange(perEvent.Intensity, perEvent.MinIntensity, perEvent.MaxIntensity);
            Assert.InRange(perEvent.Frequency, 1, stored.Frequency);
        }
    }

    private static float[] ReadAll(ISampleProvider provider)
    {
        var samples = new List<float>();
        var buffer = new float[4096];

        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.Take(read));
            if (samples.Count > SampleRate * 5) break; // safety net against an endless provider
        }

        return samples.ToArray();
    }
}
