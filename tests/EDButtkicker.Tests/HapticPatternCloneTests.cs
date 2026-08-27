using System.Text.Json;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Covers the deep clone that keeps advanced pattern semantics intact between a stored mapping and
/// the copy that gets played back. Pure model/service code - no audio device is touched.
/// </summary>
public class HapticPatternCloneTests
{
    [Fact]
    public void Clone_CopiesEveryField()
    {
        var original = BuildFullyPopulatedPattern();

        var clone = original.Clone();

        Assert.Equal(original.Name, clone.Name);
        Assert.Equal(original.Pattern, clone.Pattern);
        Assert.Equal(original.Frequency, clone.Frequency);
        Assert.Equal(original.Duration, clone.Duration);
        Assert.Equal(original.Intensity, clone.Intensity);
        Assert.Equal(original.FadeIn, clone.FadeIn);
        Assert.Equal(original.FadeOut, clone.FadeOut);
        Assert.Equal(original.IntensityFromDamage, clone.IntensityFromDamage);
        Assert.Equal(original.MaxIntensity, clone.MaxIntensity);
        Assert.Equal(original.MinIntensity, clone.MinIntensity);

        Assert.Equal(IntensityCurve.Custom, clone.IntensityCurve);
        Assert.Equal(WaveformType.Triangle, clone.Waveform);

        Assert.Equal(original.ChainedPatterns, clone.ChainedPatterns);
        Assert.Equal(original.Conditions, clone.Conditions);

        Assert.Equal(original.EnableVoiceAnnouncement, clone.EnableVoiceAnnouncement);
        Assert.Equal(original.VoiceMessage, clone.VoiceMessage);
        Assert.Equal(original.EnableAudioCue, clone.EnableAudioCue);
        Assert.Equal(original.AudioCueFile, clone.AudioCueFile);

        Assert.Equal(original.Layers.Count, clone.Layers.Count);
        for (int i = 0; i < original.Layers.Count; i++)
        {
            AssertLayersEqual(original.Layers[i], clone.Layers[i]);
        }

        Assert.Equal(original.CustomCurvePoints.Count, clone.CustomCurvePoints.Count);
        for (int i = 0; i < original.CustomCurvePoints.Count; i++)
        {
            Assert.Equal(original.CustomCurvePoints[i].Time, clone.CustomCurvePoints[i].Time);
            Assert.Equal(original.CustomCurvePoints[i].Intensity, clone.CustomCurvePoints[i].Intensity);
        }
    }

    /// <summary>
    /// Completeness guard: serializing both sides has to produce identical JSON, so a field added to
    /// HapticPattern later but forgotten in Clone() fails here instead of silently dropping at playback.
    /// </summary>
    [Fact]
    public void Clone_IsIndistinguishableFromTheOriginalWhenSerialized()
    {
        var original = BuildFullyPopulatedPattern();

        var clone = original.Clone();

        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(clone));
    }

    [Fact]
    public void Clone_SharesNoMutableReferences()
    {
        var original = BuildFullyPopulatedPattern();

        var clone = original.Clone();

        Assert.NotSame(original, clone);
        Assert.NotSame(original.Layers, clone.Layers);
        Assert.NotSame(original.ChainedPatterns, clone.ChainedPatterns);
        Assert.NotSame(original.Conditions, clone.Conditions);
        Assert.NotSame(original.CustomCurvePoints, clone.CustomCurvePoints);

        for (int i = 0; i < original.Layers.Count; i++)
        {
            Assert.NotSame(original.Layers[i], clone.Layers[i]);
        }

        for (int i = 0; i < original.CustomCurvePoints.Count; i++)
        {
            Assert.NotSame(original.CustomCurvePoints[i], clone.CustomCurvePoints[i]);
        }
    }

    [Fact]
    public void MutatingTheClone_LeavesTheOriginalUntouched()
    {
        var original = BuildFullyPopulatedPattern();
        var originalJson = JsonSerializer.Serialize(original);

        var clone = original.Clone();

        clone.Layers.Add(new PatternLayer { Frequency = 99 });
        clone.Layers[0].Frequency = 123;
        clone.Layers[0].Waveform = WaveformType.Noise;
        clone.ChainedPatterns.Add("Injected Chain");
        clone.Conditions["Injected"] = "value";
        clone.CustomCurvePoints.Add(new CurvePoint { Time = 0.5f, Intensity = 0.5f });
        clone.CustomCurvePoints[0].Intensity = 0.01f;
        clone.Waveform = WaveformType.Square;
        clone.IntensityCurve = IntensityCurve.Bounce;
        clone.VoiceMessage = "overwritten";
        clone.AudioCueFile = "overwritten.wav";

        Assert.Equal(originalJson, JsonSerializer.Serialize(original));
    }

    /// <summary>
    /// The event-specific modifiers scale intensity/frequency/duration on the copy; the stored
    /// mapping they came from has to survive untouched so the next event starts from defaults again.
    /// </summary>
    [Theory]
    [MemberData(nameof(ModifyingEvents))]
    public void CreatePatternForEvent_DoesNotMutateTheStoredMapping(JournalEvent journalEvent)
    {
        var stored = BuildFullyPopulatedPattern();
        var storedJson = JsonSerializer.Serialize(stored);

        var played = EventPatternFactory.CreatePatternForEvent(stored, journalEvent);

        Assert.NotSame(stored, played);
        Assert.Equal(storedJson, JsonSerializer.Serialize(stored));
    }

    public static TheoryData<JournalEvent> ModifyingEvents => new()
    {
        new JournalEvent
        {
            Event = "FSDJump",
            AdditionalData = new Dictionary<string, object> { ["JumpDist"] = 42.5 }
        },
        new JournalEvent { Event = "HullDamage", Health = 0.35 },
        new JournalEvent { Event = "Docked", Ship = "Anaconda" },
        new JournalEvent { Event = "Undocked", Ship = "Sidewinder" },
        new JournalEvent { Event = "ShipDestroyed" },
        new JournalEvent { Event = "Touchdown", Ship = "Corvette" },
        new JournalEvent
        {
            Event = "HeatDamage",
            AdditionalData = new Dictionary<string, object> { ["Heat"] = 0.95 }
        },
        new JournalEvent
        {
            Event = "FuelScoop",
            AdditionalData = new Dictionary<string, object> { ["Rate"] = 8.0 }
        },
        new JournalEvent { Event = "UnderAttack" },
        new JournalEvent { Event = "LaunchFighter" },
        new JournalEvent
        {
            Event = "JetConeBoost",
            AdditionalData = new Dictionary<string, object> { ["Boost"] = 4.0 }
        },
        new JournalEvent
        {
            Event = "Interdicted",
            AdditionalData = new Dictionary<string, object> { ["Success"] = false }
        },
        new JournalEvent { Event = "ShieldDown" },
        new JournalEvent { Event = "ShieldsUp" }
    };

    [Fact]
    public void CreatePatternForEvent_KeepsAdvancedFieldsAndStillAppliesModifiers()
    {
        var stored = BuildFullyPopulatedPattern();
        var journalEvent = new JournalEvent
        {
            Event = "FSDJump",
            AdditionalData = new Dictionary<string, object> { ["JumpDist"] = 100.0 }
        };

        var played = EventPatternFactory.CreatePatternForEvent(stored, journalEvent);

        // The modifier ran on the copy...
        Assert.True(played.Intensity > stored.Intensity);

        // ...and everything the old partial copy dropped is still there.
        Assert.Equal(stored.IntensityCurve, played.IntensityCurve);
        Assert.Equal(stored.Waveform, played.Waveform);
        Assert.Equal(stored.Layers.Count, played.Layers.Count);
        Assert.Equal(stored.ChainedPatterns, played.ChainedPatterns);
        Assert.Equal(stored.Conditions, played.Conditions);
        Assert.Equal(stored.CustomCurvePoints.Count, played.CustomCurvePoints.Count);
        Assert.Equal(stored.EnableVoiceAnnouncement, played.EnableVoiceAnnouncement);
        Assert.Equal(stored.VoiceMessage, played.VoiceMessage);
        Assert.Equal(stored.EnableAudioCue, played.EnableAudioCue);
        Assert.Equal(stored.AudioCueFile, played.AudioCueFile);
        AssertLayersEqual(stored.Layers[0], played.Layers[0]);
        AssertLayersEqual(stored.Layers[1], played.Layers[1]);
    }

    [Fact]
    public void CreatePatternForEvent_ForAnUnmodifiedEvent_ReturnsAnEqualCopy()
    {
        var stored = BuildFullyPopulatedPattern();

        var played = EventPatternFactory.CreatePatternForEvent(stored, new JournalEvent { Event = "Music" });

        Assert.NotSame(stored, played);
        Assert.Equal(JsonSerializer.Serialize(stored), JsonSerializer.Serialize(played));
    }

    [Fact]
    public void Clone_SurvivesAJsonRoundTrip()
    {
        var original = BuildFullyPopulatedPattern();

        var roundTripped = JsonSerializer.Deserialize<HapticPattern>(JsonSerializer.Serialize(original));

        Assert.NotNull(roundTripped);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(roundTripped!.Clone()));
    }

    private static void AssertLayersEqual(PatternLayer expected, PatternLayer actual)
    {
        Assert.Equal(expected.Waveform, actual.Waveform);
        Assert.Equal(expected.Frequency, actual.Frequency);
        Assert.Equal(expected.Amplitude, actual.Amplitude);
        Assert.Equal(expected.PhaseOffset, actual.PhaseOffset);
        Assert.Equal(expected.Curve, actual.Curve);
        Assert.Equal(expected.StartTime, actual.StartTime);
        Assert.Equal(expected.Duration, actual.Duration);
        Assert.Equal(expected.FadeIn, actual.FadeIn);
        Assert.Equal(expected.FadeOut, actual.FadeOut);
    }

    /// <summary>Every field set away from its default, so a missed copy shows up as a difference.</summary>
    internal static HapticPattern BuildFullyPopulatedPattern() => new()
    {
        Name = "Fully Populated",
        Pattern = PatternType.MultiLayer,
        Frequency = 45,
        Duration = 1500,
        Intensity = 70,
        FadeIn = 120,
        FadeOut = 200,
        IntensityFromDamage = true,
        MaxIntensity = 90,
        MinIntensity = 20,
        IntensityCurve = IntensityCurve.Custom,
        Waveform = WaveformType.Triangle,
        Layers =
        {
            new PatternLayer
            {
                Waveform = WaveformType.Square,
                Frequency = 18,
                Amplitude = 0.6f,
                PhaseOffset = 90,
                Curve = IntensityCurve.Exponential,
                StartTime = 0,
                Duration = 800,
                FadeIn = 50,
                FadeOut = 60
            },
            new PatternLayer
            {
                Waveform = WaveformType.Sawtooth,
                Frequency = 72,
                Amplitude = 0.35f,
                PhaseOffset = 180,
                Curve = IntensityCurve.Bounce,
                StartTime = 400,
                Duration = 900,
                FadeIn = 25,
                FadeOut = 75
            }
        },
        ChainedPatterns = { "Follow Up A", "Follow Up B" },
        Conditions = { ["Health"] = "<0.5", ["InCombat"] = true },
        EnableVoiceAnnouncement = true,
        VoiceMessage = "Frame shift drive charging",
        EnableAudioCue = true,
        AudioCueFile = "cues/fsd.wav",
        CustomCurvePoints =
        {
            new CurvePoint { Time = 0.0f, Intensity = 0.2f },
            new CurvePoint { Time = 0.5f, Intensity = 1.0f },
            new CurvePoint { Time = 1.0f, Intensity = 0.1f }
        }
    };
}
