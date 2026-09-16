using EDButtkicker.Hosting;
using EDButtkicker.Models;

namespace EDButtkicker.Services;

/// <summary>
/// The one gate a pattern pack passes before it is indexed or made available for playback.
/// <c>patterns/schema.json</c> documents exactly these rules for pack authors; this class is what
/// the runtime actually enforces, so the two are kept in step and a file that the schema calls
/// valid is a file the engine can play.
///
/// Deserialization already rejects a pack whose <c>pattern</c>, <c>intensityCurve</c>, <c>curve</c>
/// or <c>waveform</c> names an enum value that does not exist - <see cref="System.Text.Json"/> with
/// a <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/> throws a
/// <see cref="System.Text.Json.JsonException"/> before this runs - so what is left here are the
/// numeric ranges, the timing relationships and the shapes a converter cannot know about.
/// Size and count limits live in <see cref="PatternLimitsGuard"/> and are called through from here
/// rather than restated, so there is still only one definition of each cap.
///
/// Every diagnostic names the offending field and says what was wrong with it: a rejected pack must
/// tell its author what to edit.
/// </summary>
public static class PatternSchemaValidator
{
    // The transducer is a shaker, not a speaker: below 10Hz it moves nothing a body can feel and
    // above 100Hz it is audible buzz rather than rumble.
    public const int MinFrequencyHz = 10;
    public const int MaxFrequencyHz = 100;

    // 0% is silence, which is never what an event pattern means; the editor already refuses it.
    public const int MinIntensityPercent = 1;
    public const int MaxIntensityPercent = 100;

    // The damage-scaling floor may legitimately be 0 - that is "no damage, no shake".
    public const int MinScalingIntensityPercent = 0;

    /// <summary>Shortest pattern worth rendering; below this the envelope is all attack and release.</summary>
    public const int MinDurationMs = 50;

    /// <summary>Longest single fade. A fade longer than this is a pattern, not an edge.</summary>
    public const int MaxFadeMs = 5000;

    public const int MinPhaseOffsetDegrees = -360;
    public const int MaxPhaseOffsetDegrees = 360;

    /// <summary>
    /// The condition keys the runtime actually evaluates, in the order
    /// <see cref="PatternSequencer.EvaluateCondition"/> switches over them. A key outside this set is
    /// a typo rather than a feature: the sequencer has no branch for it and falls through to
    /// "condition met", so the pattern plays unconditionally and the author never learns why.
    /// <c>patterns/schema.json</c> lists the same eight names under
    /// <c>conditions.propertyNames</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownConditionKeys = new[]
    {
        "health_below",
        "health_above",
        "ship_type",
        "event_frequency",
        "time_of_day",
        "session_duration",
        "hull_damage_above",
        "in_combat"
    };

    // The sequencer lowercases a key before matching it, so "Health_Below" reaches the same branch
    // "health_below" does and must not be reported as unknown.
    private static readonly HashSet<string> RecognisedConditionKeys =
        new(KnownConditionKeys, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every rule the pack breaks, or an empty list when it breaks none.</summary>
    public static IReadOnlyList<string> Validate(PatternFile patternFile)
    {
        var errors = new List<string>();

        ValidateSchemaVersion(errors, patternFile.SchemaVersion);

        if (patternFile.Metadata == null)
        {
            errors.Add("metadata: a pattern file must carry a metadata block");
        }
        else if (string.IsNullOrWhiteSpace(patternFile.Metadata.Name))
        {
            errors.Add("metadata.name: a pattern pack must be named");
        }

        if (patternFile.Ships == null || patternFile.Ships.Count == 0)
        {
            errors.Add("ships: a pattern file must define at least one ship");
            return errors;
        }

        foreach (var ship in patternFile.Ships)
        {
            if (string.IsNullOrWhiteSpace(ship.Key))
            {
                errors.Add("ships: a ship key may not be blank");
                continue;
            }

            // A ship with no events is a placeholder, not a broken file - the editor writes one the
            // moment a pack is created.
            if (ship.Value?.Events == null)
            {
                continue;
            }

            foreach (var eventPattern in ship.Value.Events)
            {
                var description = $"ship '{ship.Key}' event '{eventPattern.Key}'";

                if (eventPattern.Value == null)
                {
                    errors.Add($"{description}: event has no pattern");
                    continue;
                }

                errors.AddRange(ValidatePattern(eventPattern.Value, description));
            }
        }

        return errors;
    }

    /// <summary>Every rule a single pattern breaks, named by <paramref name="description"/>.</summary>
    public static IReadOnlyList<string> ValidatePattern(HapticPattern pattern, string description = "Pattern")
    {
        var errors = new List<string>();

        // Size, count and maximum-duration caps, defined once in the limits guard.
        errors.AddRange(PatternLimitsGuard.Validate(pattern, description));

        if (pattern.Frequency < MinFrequencyHz || pattern.Frequency > MaxFrequencyHz)
        {
            errors.Add($"{description}: frequency is {pattern.Frequency}Hz, outside the supported {MinFrequencyHz}-{MaxFrequencyHz}Hz range");
        }

        if (pattern.Intensity < MinIntensityPercent || pattern.Intensity > MaxIntensityPercent)
        {
            errors.Add($"{description}: intensity is {pattern.Intensity}%, outside the supported {MinIntensityPercent}-{MaxIntensityPercent}% range");
        }

        if (pattern.Duration < MinDurationMs)
        {
            errors.Add($"{description}: duration is {pattern.Duration}ms, below the {MinDurationMs}ms minimum");
        }

        ValidateFade(errors, pattern.FadeIn, "fadeIn", description);
        ValidateFade(errors, pattern.FadeOut, "fadeOut", description);

        // The envelope is attack, sustain, release. If the two fades fill the whole pattern there is
        // no sustain left and the pattern never reaches the intensity it asked for.
        if (pattern.FadeIn >= 0 && pattern.FadeOut >= 0 && pattern.Duration >= MinDurationMs &&
            pattern.FadeIn + pattern.FadeOut > pattern.Duration)
        {
            errors.Add($"{description}: fadeIn ({pattern.FadeIn}ms) + fadeOut ({pattern.FadeOut}ms) exceeds duration ({pattern.Duration}ms)");
        }

        ValidateScalingIntensity(errors, pattern.MinIntensity, "minIntensity", description);
        ValidateScalingIntensity(errors, pattern.MaxIntensity, "maxIntensity", description);

        if (pattern.MinIntensity > pattern.MaxIntensity)
        {
            errors.Add($"{description}: minIntensity ({pattern.MinIntensity}%) is greater than maxIntensity ({pattern.MaxIntensity}%)");
        }

        ValidateLayers(errors, pattern, description);
        ValidateCurvePoints(errors, pattern, description);

        foreach (var chained in pattern.ChainedPatterns ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(chained))
            {
                // chainedPatterns holds pattern names to look up, so a blank entry chains nothing.
                errors.Add($"{description}: chainedPatterns contains a blank pattern name");
            }
        }

        ValidateConditions(errors, pattern, description);

        return errors;
    }

    private static void ValidateSchemaVersion(List<string> errors, string? declared)
    {
        // The compatibility rule itself lives in PatternSchemaVersion, so the loader's gate and this
        // one can never disagree about which packs this build reads. An older-but-supported version
        // passes here: the loader migrates a pack to the current representation before validating it,
        // and a pack handed straight to the validator is judged on the shape it actually has.
        switch (PatternSchemaVersion.Classify(declared, out _))
        {
            case PatternSchemaCompatibility.Current:
            case PatternSchemaCompatibility.Migratable:
                return;

            case PatternSchemaCompatibility.TooNew:
                errors.Add($"schemaVersion: '{declared}' is newer than this build reads; the highest supported pattern pack schema is v{PatternSchemaVersion.Current}");
                return;

            case PatternSchemaCompatibility.TooOld:
                errors.Add($"schemaVersion: '{declared}' is older than the oldest pattern pack schema this build can migrate; this build reads v{PatternSchemaVersion.OldestSupported} through v{PatternSchemaVersion.Current}");
                return;

            default:
                errors.Add($"schemaVersion: '{declared}' is not a version number; this build reads pattern pack schema v{PatternSchemaVersion.OldestSupported} through v{PatternSchemaVersion.Current}, written as 'major' or 'major.minor'");
                return;
        }
    }

    private static void ValidateFade(List<string> errors, int fade, string field, string description)
    {
        if (fade < 0 || fade > MaxFadeMs)
        {
            errors.Add($"{description}: {field} is {fade}ms, outside the supported 0-{MaxFadeMs}ms range");
        }
    }

    private static void ValidateScalingIntensity(List<string> errors, int intensity, string field, string description)
    {
        if (intensity < MinScalingIntensityPercent || intensity > MaxIntensityPercent)
        {
            errors.Add($"{description}: {field} is {intensity}%, outside the supported {MinScalingIntensityPercent}-{MaxIntensityPercent}% range");
        }
    }

    /// <summary>
    /// A pattern with no conditions is the common case and always fine. A pattern gated on a key the
    /// sequencer does not know is not: the unknown key evaluates to "met", so the gate the author
    /// wrote silently does nothing. Rejecting it here turns a pattern that quietly plays at the wrong
    /// moment into a diagnostic that names the misspelling.
    /// </summary>
    private static void ValidateConditions(List<string> errors, HapticPattern pattern, string description)
    {
        var conditions = pattern.Conditions;

        if (conditions == null || conditions.Count == 0)
        {
            return;
        }

        foreach (var condition in conditions)
        {
            if (string.IsNullOrWhiteSpace(condition.Key))
            {
                errors.Add($"{description}: conditions contains a blank property name");
            }
            else if (!RecognisedConditionKeys.Contains(condition.Key))
            {
                errors.Add($"{description}: condition key '{condition.Key}' is not a recognised condition; valid keys are: {string.Join(", ", KnownConditionKeys)}");
            }
        }
    }

    private static void ValidateLayers(List<string> errors, HapticPattern pattern, string description)
    {
        var layers = pattern.Layers;

        if (layers == null)
        {
            return;
        }

        for (var index = 0; index < layers.Count; index++)
        {
            var layer = layers[index];
            var layerDescription = $"{description} layer {index}";

            if (layer == null)
            {
                errors.Add($"{layerDescription}: layer is empty");
                continue;
            }

            if (layer.Frequency < MinFrequencyHz || layer.Frequency > MaxFrequencyHz)
            {
                errors.Add($"{layerDescription}: frequency is {layer.Frequency}Hz, outside the supported {MinFrequencyHz}-{MaxFrequencyHz}Hz range");
            }

            if (!float.IsFinite(layer.Amplitude) || layer.Amplitude < 0f || layer.Amplitude > 1f)
            {
                errors.Add($"{layerDescription}: amplitude is {layer.Amplitude}, outside the supported 0-1 range");
            }

            if (layer.PhaseOffset < MinPhaseOffsetDegrees || layer.PhaseOffset > MaxPhaseOffsetDegrees)
            {
                errors.Add($"{layerDescription}: phaseOffset is {layer.PhaseOffset} degrees, outside the supported {MinPhaseOffsetDegrees}-{MaxPhaseOffsetDegrees} range");
            }

            if (layer.StartTime < 0)
            {
                errors.Add($"{layerDescription}: startTime is {layer.StartTime}ms, which is before the pattern begins");
            }
            else if (pattern.Duration > 0 && layer.StartTime >= pattern.Duration)
            {
                // A layer that starts after the pattern has ended is silently never heard.
                errors.Add($"{layerDescription}: startTime is {layer.StartTime}ms, at or past the pattern duration ({pattern.Duration}ms)");
            }

            if (layer.Duration < 0)
            {
                errors.Add($"{layerDescription}: duration is {layer.Duration}ms, which is negative");
            }

            ValidateFade(errors, layer.FadeIn, "fadeIn", layerDescription);
            ValidateFade(errors, layer.FadeOut, "fadeOut", layerDescription);

            // 0 means "run to the end of the pattern", so that is the span the fades have to fit in.
            var effectiveDuration = layer.Duration > 0
                ? layer.Duration
                : Math.Max(0, pattern.Duration - Math.Max(0, layer.StartTime));

            if (layer.FadeIn >= 0 && layer.FadeOut >= 0 && effectiveDuration > 0 &&
                layer.FadeIn + layer.FadeOut > effectiveDuration)
            {
                errors.Add($"{layerDescription}: fadeIn ({layer.FadeIn}ms) + fadeOut ({layer.FadeOut}ms) exceeds the layer's {effectiveDuration}ms span");
            }
        }
    }

    private static void ValidateCurvePoints(List<string> errors, HapticPattern pattern, string description)
    {
        var points = pattern.CustomCurvePoints;

        if (points == null || points.Count == 0)
        {
            // A Custom curve with no points has nothing to interpolate between.
            if (pattern.IntensityCurve == IntensityCurve.Custom)
            {
                errors.Add($"{description}: intensityCurve is Custom but customCurvePoints is empty");
            }

            return;
        }

        if (pattern.IntensityCurve == IntensityCurve.Custom && points.Count < 2)
        {
            errors.Add($"{description}: intensityCurve is Custom but customCurvePoints has only {points.Count} point; at least 2 are needed to interpolate");
        }

        var previousTime = float.NegativeInfinity;

        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var pointDescription = $"{description} customCurvePoints[{index}]";

            if (point == null)
            {
                errors.Add($"{pointDescription}: curve point is empty");
                continue;
            }

            if (!float.IsFinite(point.Time) || point.Time < 0f || point.Time > 1f)
            {
                errors.Add($"{pointDescription}: time is {point.Time}, outside the supported 0-1 range");
            }
            else if (point.Time < previousTime)
            {
                // The curve is walked in order, so an out-of-order point would be skipped.
                errors.Add($"{pointDescription}: time {point.Time} goes backwards; curve points must be ordered by time");
            }

            if (!float.IsFinite(point.Intensity) || point.Intensity < 0f || point.Intensity > 1f)
            {
                errors.Add($"{pointDescription}: intensity is {point.Intensity}, outside the supported 0-1 range");
            }

            previousTime = point.Time;
        }
    }
}
