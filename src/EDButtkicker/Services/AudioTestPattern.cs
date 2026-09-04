using EDButtkicker.Configuration;
using EDButtkicker.Models;

namespace EDButtkicker.Services;

/// <summary>
/// The tone every "test" button plays. A buttkicker is a physical actuator sitting under the user's
/// seat and a test click is exactly the moment their amplifier gain is still unknown, so the level
/// is capped here rather than at each call site: the configured maximum only ever lowers it.
/// </summary>
public static class AudioTestPattern
{
    public const int IntensityPercent = 30;
    public const int DurationMs = 800;
    public const int FadeInMs = 200;
    public const int FadeOutMs = 300;
    public const int MinFrequency = 20;
    public const int MaxFrequency = 50;

    /// <summary>The level a test tone plays at: never above the cap, never above the user's own.</summary>
    public static int IntensityFor(AudioSettings audio) =>
        Math.Min(IntensityPercent, Math.Max(1, audio.MaxIntensity));

    public static HapticPattern Create(AudioSettings audio, string name) => new()
    {
        Name = name,
        Pattern = PatternType.SustainedRumble,
        Frequency = Math.Clamp(audio.DefaultFrequency, MinFrequency, MaxFrequency),
        Duration = DurationMs,
        Intensity = IntensityFor(audio),
        FadeIn = FadeInMs,
        FadeOut = FadeOutMs
    };
}
