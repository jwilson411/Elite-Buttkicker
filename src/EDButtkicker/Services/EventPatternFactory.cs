using Microsoft.Extensions.Logging;
using EDButtkicker.Models;

namespace EDButtkicker.Services;

/// <summary>
/// Builds the per-event copy of a stored haptic pattern. The stored mapping is deep cloned first and
/// every event-specific adjustment is applied to that clone only, so defaults survive between events.
/// Deliberately free of any audio device dependency so the whole path can be exercised in tests.
/// </summary>
public static class EventPatternFactory
{
    public static HapticPattern CreatePatternForEvent(HapticPattern basePattern, JournalEvent journalEvent, ILogger? logger = null)
    {
        // Deep clone keeps layers, chains, conditions, curves and cue settings intact for playback.
        var pattern = basePattern.Clone();

        ApplyEventSpecificModifications(pattern, journalEvent, logger);

        return pattern;
    }

    private static void ApplyEventSpecificModifications(HapticPattern pattern, JournalEvent journalEvent, ILogger? logger)
    {
        switch (journalEvent.Event)
        {
            case "FSDJump":
                ApplyFSDJumpModifications(pattern, journalEvent, logger);
                break;

            case "HullDamage":
                ApplyHullDamageModifications(pattern, journalEvent, logger);
                break;

            case "Docked":
            case "Undocked":
                ApplyDockingModifications(pattern, journalEvent);
                break;

            case "ShipTargeted":
                ApplyTargetingModifications(journalEvent, logger);
                break;

            case "FighterDestroyed":
            case "ShipDestroyed":
                ApplyExplosionModifications(pattern);
                break;

            case "Touchdown":
            case "Liftoff":
                ApplyPlanetaryModifications(pattern, journalEvent, logger);
                break;

            case "HeatWarning":
            case "HeatDamage":
                ApplyHeatModifications(pattern, journalEvent, logger);
                break;

            case "FuelScoop":
                ApplyFuelScoopModifications(pattern, journalEvent, logger);
                break;

            case "UnderAttack":
                ApplyUnderAttackModifications(pattern, journalEvent, logger);
                break;

            case "LaunchFighter":
            case "DockFighter":
                ApplyFighterModifications(pattern, journalEvent, logger);
                break;

            case "JetConeBoost":
                ApplyNeutronBoostModifications(pattern, journalEvent, logger);
                break;

            case "Interdicted":
            case "Interdiction":
                ApplyInterdictionModifications(pattern, journalEvent, logger);
                break;

            case "ShieldDown":
            case "ShieldsUp":
                ApplyShieldModifications(pattern, journalEvent);
                break;
        }
    }

    private static void ApplyFSDJumpModifications(HapticPattern pattern, JournalEvent journalEvent, ILogger? logger)
    {
        // Longer buildup for interdiction vs normal jump
        if (journalEvent.AdditionalData?.ContainsKey("JumpDist") == true)
        {
            try
            {
                var jumpDist = Convert.ToDouble(journalEvent.AdditionalData["JumpDist"]);
                // Scale intensity slightly based on jump distance (longer = more intense)
                var distanceMultiplier = Math.Min(1.3, 1.0 + (jumpDist / 100.0) * 0.3);
                pattern.Intensity = (int)(pattern.Intensity * distanceMultiplier);

                logger?.LogDebug("FSD Jump distance: {Distance} Ly, intensity multiplier: {Multiplier}",
                    jumpDist, distanceMultiplier);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Error parsing jump distance");
            }
        }
    }

    private static void ApplyHullDamageModifications(HapticPattern pattern, JournalEvent journalEvent, ILogger? logger)
    {
        if (journalEvent.Health.HasValue)
        {
            // Scale frequency based on remaining health (lower health = lower frequency)
            var healthPercent = journalEvent.Health.Value;
            var freqMultiplier = 0.7 + (healthPercent * 0.3); // 0.7 to 1.0 range
            pattern.Frequency = (int)(pattern.Frequency * freqMultiplier);

            logger?.LogDebug("Hull damage - Health: {Health}%, frequency: {Frequency}Hz",
                healthPercent * 100, pattern.Frequency);
        }
    }

    private static void ApplyDockingModifications(HapticPattern pattern, JournalEvent journalEvent)
    {
        // Adjust based on ship size/mass if available
        if (!string.IsNullOrEmpty(journalEvent.Ship))
        {
            // Larger ships get slightly more intense docking feedback
            var shipType = journalEvent.Ship.ToLower();
            if (shipType.Contains("anaconda") || shipType.Contains("corvette") || shipType.Contains("cutter"))
            {
                pattern.Intensity = (int)(pattern.Intensity * 1.2);
                pattern.Duration = (int)(pattern.Duration * 1.1);
            }
            else if (shipType.Contains("sidewinder") || shipType.Contains("eagle") || shipType.Contains("hauler"))
            {
                pattern.Intensity = (int)(pattern.Intensity * 0.8);
                pattern.Duration = (int)(pattern.Duration * 0.9);
            }
        }
    }

    private static void ApplyTargetingModifications(JournalEvent journalEvent, ILogger? logger)
    {
        // Quick, subtle pulse for targeting
        // Could differentiate between ship types if target info is available
        if (!string.IsNullOrEmpty(journalEvent.Target))
        {
            logger?.LogDebug("Target acquired: {Target}", journalEvent.Target);
        }
    }

    private static void ApplyExplosionModifications(HapticPattern pattern)
    {
        // More intense explosion for larger ships
        pattern.Intensity = Math.Min(100, (int)(pattern.Intensity * 1.1));
    }

    private static void ApplyPlanetaryModifications(HapticPattern pattern, JournalEvent journalEvent, ILogger? logger)
    {
        // Adjust based on ship mass and planetary gravity if available
        if (!string.IsNullOrEmpty(journalEvent.Ship))
        {
            var shipType = journalEvent.Ship.ToLower();
            if (shipType.Contains("anaconda") || shipType.Contains("corvette") || shipType.Contains("cutter"))
            {
                // Heavy ships have more impact
                pattern.Intensity = (int)(pattern.Intensity * 1.3);
                pattern.Frequency = Math.Max(20, pattern.Frequency - 5); // Lower frequency for heavy ships
            }
            else if (shipType.Contains("sidewinder") || shipType.Contains("eagle") || shipType.Contains("courier"))
            {
                // Light ships have lighter impact
                pattern.Intensity = (int)(pattern.Intensity * 0.7);
                pattern.Frequency = Math.Min(60, pattern.Frequency + 5); // Higher frequency for light ships
            }
        }

        // Check for planetary body information
        if (journalEvent.AdditionalData?.ContainsKey("Body") == true)
        {
            logger?.LogDebug("Planetary event on body: {Body}", journalEvent.AdditionalData["Body"]);
        }
    }

    private static void ApplyHeatModifications(HapticPattern pattern, JournalEvent journalEvent, ILogger? logger)
    {
        // Scale intensity based on heat level if available
        if (journalEvent.AdditionalData?.ContainsKey("Heat") == true)
        {
            try
            {
                var heatLevel = Convert.ToDouble(journalEvent.AdditionalData["Heat"]);
                if (heatLevel > 0.8) // Above 80% heat
                {
                    pattern.Intensity = Math.Min(100, (int)(pattern.Intensity * 1.4));
                    pattern.Frequency = Math.Min(80, pattern.Frequency + 10); // Higher frequency for critical heat
                }
                else if (heatLevel > 0.6) // Above 60% heat
                {
                    pattern.Intensity = Math.Min(100, (int)(pattern.Intensity * 1.2));
                    pattern.Frequency = Math.Min(70, pattern.Frequency + 5);
                }

                logger?.LogDebug("Heat event - Level: {Heat}%, intensity: {Intensity}%",
                    heatLevel * 100, pattern.Intensity);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Error parsing heat level");
            }
        }
    }

    private static void ApplyFuelScoopModifications(HapticPattern pattern, JournalEvent journalEvent, ILogger? logger)
    {
        // Adjust based on scoop rate and fuel level
        if (journalEvent.AdditionalData?.ContainsKey("Rate") == true)
        {
            try
            {
                var scoopRate = Convert.ToDouble(journalEvent.AdditionalData["Rate"]);
                // Higher scoop rate = more intensity
                var rateMultiplier = Math.Min(1.5, 1.0 + (scoopRate / 10.0) * 0.5);
                pattern.Intensity = (int)(pattern.Intensity * rateMultiplier);

                logger?.LogDebug("Fuel scoop rate: {Rate} kg/s, intensity multiplier: {Multiplier}",
                    scoopRate, rateMultiplier);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Error parsing scoop rate");
            }
        }
    }

    private static void ApplyUnderAttackModifications(HapticPattern pattern, JournalEvent journalEvent, ILogger? logger)
    {
        // Intense, immediate feedback for combat
        pattern.Intensity = Math.Min(100, pattern.Intensity + 10);

        // Check for target information if available
        if (journalEvent.AdditionalData?.ContainsKey("Target") == true)
        {
            var target = journalEvent.AdditionalData["Target"]?.ToString();
            if (!string.IsNullOrEmpty(target))
            {
                logger?.LogDebug("Under attack by: {Target}", target);

                // More intense for larger attackers
                if (target.ToLower().Contains("anaconda") || target.ToLower().Contains("corvette"))
                {
                    pattern.Intensity = Math.Min(100, (int)(pattern.Intensity * 1.2));
                }
            }
        }
    }

    private static void ApplyFighterModifications(HapticPattern pattern, JournalEvent journalEvent, ILogger? logger)
    {
        // Fighter operations are generally lighter events
        pattern.Intensity = (int)(pattern.Intensity * 0.9);

        if (journalEvent.AdditionalData?.ContainsKey("ID") == true)
        {
            var fighterId = journalEvent.AdditionalData["ID"]?.ToString();
            logger?.LogDebug("Fighter operation - ID: {FighterID}", fighterId);
        }
    }

    private static void ApplyNeutronBoostModifications(HapticPattern pattern, JournalEvent journalEvent, ILogger? logger)
    {
        // Neutron boost should be intense and unique
        pattern.Intensity = Math.Min(100, pattern.Intensity + 15);

        // Check boost multiplier if available
        if (journalEvent.AdditionalData?.ContainsKey("Boost") == true)
        {
            try
            {
                var boostValue = Convert.ToDouble(journalEvent.AdditionalData["Boost"]);
                // Higher boost = longer duration and more intensity
                if (boostValue > 2.0)
                {
                    pattern.Duration = (int)(pattern.Duration * 1.2);
                    pattern.Intensity = Math.Min(100, (int)(pattern.Intensity * 1.1));
                }

                logger?.LogDebug("Neutron boost: {Boost}x FSD range", boostValue);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Error parsing boost value");
            }
        }
    }

    private static void ApplyInterdictionModifications(HapticPattern pattern, JournalEvent journalEvent, ILogger? logger)
    {
        // Interdiction events should be stressful and noticeable
        if (journalEvent.Event == "Interdicted")
        {
            // Being interdicted is more stressful
            pattern.Intensity = Math.Min(100, pattern.Intensity + 20);
        }
        else if (journalEvent.Event == "Interdiction")
        {
            // Interdicting someone else is slightly less intense
            pattern.Intensity = Math.Min(100, pattern.Intensity + 10);
        }

        // Check for interdiction success/failure
        if (journalEvent.AdditionalData?.ContainsKey("Success") == true)
        {
            var success = Convert.ToBoolean(journalEvent.AdditionalData["Success"]);
            logger?.LogDebug("Interdiction {Result}", success ? "successful" : "failed");

            if (!success && journalEvent.Event == "Interdicted")
            {
                // Failed interdiction attempt (escaped) - less intense
                pattern.Intensity = (int)(pattern.Intensity * 0.7);
                pattern.Duration = (int)(pattern.Duration * 0.8);
            }
        }

        // Check for interdicting ship
        if (journalEvent.AdditionalData?.ContainsKey("Interdictor") == true)
        {
            var interdictor = journalEvent.AdditionalData["Interdictor"]?.ToString();
            logger?.LogDebug("Interdicted by: {Interdictor}", interdictor);
        }
    }

    private static void ApplyShieldModifications(HapticPattern pattern, JournalEvent journalEvent)
    {
        if (journalEvent.Event == "ShieldDown")
        {
            // Shields going down is critical - high intensity
            pattern.Intensity = Math.Min(100, pattern.Intensity + 15);
        }
        else if (journalEvent.Event == "ShieldsUp")
        {
            // Shields coming online is positive but less urgent
            pattern.Intensity = (int)(pattern.Intensity * 0.8);
        }
    }
}
