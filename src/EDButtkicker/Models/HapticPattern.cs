namespace EDButtkicker.Models;

public enum PatternType
{
    SharpPulse,
    BuildupRumble,
    SustainedRumble,
    Oscillating,
    Impact,
    Fade,
    MultiLayer,
    Sequence
}

public enum IntensityCurve
{
    Linear,
    Exponential,
    Logarithmic,
    Sine,
    Bounce,
    Custom
}

public enum WaveformType
{
    Sine,
    Square,
    Triangle,
    Sawtooth,
    Noise
}

public class PatternLayer
{
    public WaveformType Waveform { get; set; } = WaveformType.Sine;
    public int Frequency { get; set; } = 40;
    public float Amplitude { get; set; } = 1.0f; // 0.0 to 1.0
    public int PhaseOffset { get; set; } = 0; // degrees
    public IntensityCurve Curve { get; set; } = IntensityCurve.Linear;
    
    // Timing properties for sequenced patterns
    public int StartTime { get; set; } = 0; // milliseconds from pattern start
    public int Duration { get; set; } = 0; // milliseconds (0 = use pattern duration)
    public int FadeIn { get; set; } = 0; // milliseconds
    public int FadeOut { get; set; } = 0; // milliseconds

    public PatternLayer Clone() => new()
    {
        Waveform = Waveform,
        Frequency = Frequency,
        Amplitude = Amplitude,
        PhaseOffset = PhaseOffset,
        Curve = Curve,
        StartTime = StartTime,
        Duration = Duration,
        FadeIn = FadeIn,
        FadeOut = FadeOut
    };
}

public class HapticPattern
{
    public string Name { get; set; } = string.Empty;
    public PatternType Pattern { get; set; }
    
    // Basic Properties
    public int Frequency { get; set; } = 40; // Hz
    public int Duration { get; set; } = 1000; // milliseconds
    public int Intensity { get; set; } = 50; // 0-100%
    public int FadeIn { get; set; } = 0; // milliseconds
    public int FadeOut { get; set; } = 0; // milliseconds
    public bool IntensityFromDamage { get; set; } = false;
    public int MaxIntensity { get; set; } = 100;
    public int MinIntensity { get; set; } = 10;
    
    // Advanced Pattern Features
    public IntensityCurve IntensityCurve { get; set; } = IntensityCurve.Linear;
    public WaveformType Waveform { get; set; } = WaveformType.Sine;
    public List<PatternLayer> Layers { get; set; } = new();
    public List<string> ChainedPatterns { get; set; } = new(); // Pattern names to chain
    public Dictionary<string, object> Conditions { get; set; } = new(); // Conditional logic
    
    // Voice Integration
    public bool EnableVoiceAnnouncement { get; set; } = false;
    public string VoiceMessage { get; set; } = string.Empty;
    public bool EnableAudioCue { get; set; } = false;
    public string AudioCueFile { get; set; } = string.Empty;
    
    // Custom Curve Points (for Custom curve type)
    public List<CurvePoint> CustomCurvePoints { get; set; } = new();

    /// <summary>
    /// Deep copy of the whole pattern, including every advanced field. Callers that adjust a pattern
    /// for a single event work on a clone so the stored mapping keeps its defaults.
    /// Nested lists and their elements are copied; <see cref="Conditions"/> gets a fresh dictionary
    /// (its values are JSON scalars/elements, which are not mutated anywhere).
    /// </summary>
    public HapticPattern Clone() => new()
    {
        Name = Name,
        Pattern = Pattern,
        Frequency = Frequency,
        Duration = Duration,
        Intensity = Intensity,
        FadeIn = FadeIn,
        FadeOut = FadeOut,
        IntensityFromDamage = IntensityFromDamage,
        MaxIntensity = MaxIntensity,
        MinIntensity = MinIntensity,
        IntensityCurve = IntensityCurve,
        Waveform = Waveform,
        Layers = Layers.Select(layer => layer.Clone()).ToList(),
        ChainedPatterns = new List<string>(ChainedPatterns),
        Conditions = new Dictionary<string, object>(Conditions),
        EnableVoiceAnnouncement = EnableVoiceAnnouncement,
        VoiceMessage = VoiceMessage,
        EnableAudioCue = EnableAudioCue,
        AudioCueFile = AudioCueFile,
        CustomCurvePoints = CustomCurvePoints.Select(point => point.Clone()).ToList()
    };
}

public class CurvePoint
{
    public float Time { get; set; } // 0.0 to 1.0 (percentage of duration)
    public float Intensity { get; set; } // 0.0 to 1.0 (percentage of max intensity)

    public CurvePoint Clone() => new() { Time = Time, Intensity = Intensity };
}

public class EventMapping
{
    public string EventType { get; set; } = string.Empty;
    public HapticPattern Pattern { get; set; } = new();
    public bool Enabled { get; set; } = true;
}