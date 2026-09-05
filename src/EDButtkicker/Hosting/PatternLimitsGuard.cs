using EDButtkicker.Controllers;
using EDButtkicker.Models;

namespace EDButtkicker.Hosting;

/// <summary>
/// The content limits on a pattern that arrives from outside: how long it may play, how many layers,
/// chained names and curve points it may carry, and how much text it may bring with it. A pack is
/// refused rather than trimmed, so nothing a user sent is silently thrown away.
/// </summary>
public static class PatternLimitsGuard
{
    /// <summary>Every limit an imported pattern pack breaks, or an empty list when it breaks none.</summary>
    public static IReadOnlyList<string> Validate(PatternFileDefinition patternFile)
    {
        var errors = new List<string>();

        if (patternFile.Metadata != null)
        {
            CheckText(errors, patternFile.Metadata.Name, "Pack name", RequestLimits.MaxStringLength);
            CheckText(errors, patternFile.Metadata.Author, "Author", RequestLimits.MaxStringLength);
            CheckText(errors, patternFile.Metadata.Description, "Description", RequestLimits.MaxStringLength);
            CheckText(errors, patternFile.Metadata.Version, "Version", RequestLimits.MaxStringLength);

            if (patternFile.Metadata.Tags is { Count: > RequestLimits.MaxTags })
            {
                errors.Add($"A pattern pack may not carry more than {RequestLimits.MaxTags} tags");
            }
        }

        if (patternFile.Ships == null)
        {
            return errors;
        }

        if (patternFile.Ships.Count > RequestLimits.MaxShipsPerPack)
        {
            errors.Add($"A pattern pack may not define more than {RequestLimits.MaxShipsPerPack} ships");
            return errors;
        }

        foreach (var ship in patternFile.Ships)
        {
            CheckText(errors, ship.Key, "Ship type", RequestLimits.MaxStringLength);
            CheckText(errors, ship.Value.DisplayName, $"Ship '{ship.Key}' display name", RequestLimits.MaxStringLength);

            if (ship.Value.Events == null)
            {
                continue;
            }

            if (ship.Value.Events.Count > RequestLimits.MaxEventsPerShip)
            {
                errors.Add($"Ship '{ship.Key}' may not define more than {RequestLimits.MaxEventsPerShip} events");
                continue;
            }

            foreach (var eventPattern in ship.Value.Events)
            {
                errors.AddRange(Validate(eventPattern.Value, $"Ship '{ship.Key}' event '{eventPattern.Key}'"));
            }
        }

        return errors;
    }

    /// <summary>Every limit a single pattern breaks, named by <paramref name="description"/>.</summary>
    public static IReadOnlyList<string> Validate(HapticPattern pattern, string description = "Pattern")
    {
        var errors = new List<string>();

        if (pattern.Duration > RequestLimits.MaxPatternDurationMs)
        {
            errors.Add($"{description}: Duration must not exceed {RequestLimits.MaxPatternDurationMs}ms");
        }

        if (pattern.Layers is { Count: > RequestLimits.MaxPatternLayers })
        {
            errors.Add($"{description}: A pattern may not define more than {RequestLimits.MaxPatternLayers} layers");
        }

        if (pattern.ChainedPatterns is { Count: > RequestLimits.MaxChainedPatterns })
        {
            errors.Add($"{description}: A pattern may not chain more than {RequestLimits.MaxChainedPatterns} patterns");
        }

        if (pattern.CustomCurvePoints is { Count: > RequestLimits.MaxCurvePoints })
        {
            errors.Add($"{description}: A pattern may not carry more than {RequestLimits.MaxCurvePoints} curve points");
        }

        if (pattern.Conditions is { Count: > RequestLimits.MaxConditions })
        {
            errors.Add($"{description}: A pattern may not carry more than {RequestLimits.MaxConditions} conditions");
        }

        CheckText(errors, pattern.Name, $"{description}: Name", RequestLimits.MaxStringLength);
        CheckText(errors, pattern.VoiceMessage, $"{description}: Voice message", RequestLimits.MaxStringLength);
        CheckText(errors, pattern.AudioCueFile, $"{description}: Audio cue file", RequestLimits.MaxPathLength);

        foreach (var chained in pattern.ChainedPatterns ?? new List<string>())
        {
            CheckText(errors, chained, $"{description}: Chained pattern name", RequestLimits.MaxStringLength);
        }

        return errors;
    }

    /// <summary>Every limit a new-pack request breaks, before anything is created from it.</summary>
    public static IReadOnlyList<string> Validate(CreatePatternRequest request)
    {
        var errors = new List<string>();

        CheckText(errors, request.PackName, "Pack name", RequestLimits.MaxStringLength);
        CheckText(errors, request.Author, "Author", RequestLimits.MaxStringLength);
        CheckText(errors, request.Description, "Description", RequestLimits.MaxStringLength);
        CheckText(errors, request.InitialShipType, "Ship type", RequestLimits.MaxStringLength);
        CheckText(errors, request.InitialShipDisplayName, "Ship display name", RequestLimits.MaxStringLength);

        if (request.Tags is { Count: > RequestLimits.MaxTags })
        {
            errors.Add($"A pattern pack may not carry more than {RequestLimits.MaxTags} tags");
        }

        return errors;
    }

    private static void CheckText(List<string> errors, string? value, string description, int limit)
    {
        if (value != null && value.Length > limit)
        {
            errors.Add($"{description} must not exceed {limit} characters");
        }
    }
}
