using EDButtkicker.Services;

namespace EDButtkicker.Models;

public class ShipSpecificPatterns
{
    public string ShipKey { get; set; } = string.Empty;
    public string ShipType { get; set; } = string.Empty;
    public string ShipName { get; set; } = string.Empty;
    public Dictionary<string, HapticPattern> EventPatterns { get; set; } = new();
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;

    public HapticPattern? GetPatternForEvent(string eventName)
    {
        return EventPatterns.TryGetValue(eventName, out var pattern) ? pattern : null;
    }

    public void SetPatternForEvent(string eventName, HapticPattern pattern)
    {
        EventPatterns[eventName] = pattern;
        LastModified = DateTime.UtcNow;
    }

    public void RemovePatternForEvent(string eventName)
    {
        if (EventPatterns.Remove(eventName))
        {
            LastModified = DateTime.UtcNow;
        }
    }

    public bool HasCustomPatternForEvent(string eventName)
    {
        return EventPatterns.ContainsKey(eventName);
    }
}

public class ShipPatternLibrary
{
    public Dictionary<string, ShipSpecificPatterns> Ships { get; set; } = new();
    public string? CurrentShipKey { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    public ShipSpecificPatterns? GetCurrentShipPatterns()
    {
        if (string.IsNullOrEmpty(CurrentShipKey))
            return null;
            
        return Ships.TryGetValue(CurrentShipKey, out var patterns) ? patterns : null;
    }

    public ShipSpecificPatterns GetOrCreateShipPatterns(CurrentShip ship)
    {
        var shipKey = ship.GetShipKey();
        
        if (!Ships.TryGetValue(shipKey, out var patterns))
        {
            patterns = new ShipSpecificPatterns
            {
                ShipKey = shipKey,
                ShipType = ship.ShipType,
                ShipName = ship.ShipName,
                IsActive = true
            };
            Ships[shipKey] = patterns;
            LastUpdated = DateTime.UtcNow;
        }
        else
        {
            // Update ship info in case name changed
            patterns.ShipType = ship.ShipType;
            patterns.ShipName = ship.ShipName;
        }

        return patterns;
    }

    public void SetCurrentShip(CurrentShip ship)
    {
        var shipKey = ship.GetShipKey();
        CurrentShipKey = shipKey;
        
        // Ensure the ship exists in the library
        GetOrCreateShipPatterns(ship);
        
        LastUpdated = DateTime.UtcNow;
    }

    public HapticPattern? GetPatternForCurrentShip(string eventName, HapticPattern? defaultPattern = null)
    {
        var currentPatterns = GetCurrentShipPatterns();
        
        // Try to get ship-specific pattern first
        var shipPattern = currentPatterns?.GetPatternForEvent(eventName);
        if (shipPattern != null)
        {
            return shipPattern;
        }
        
        // Fall back to default pattern
        return defaultPattern;
    }

    public List<ShipSpecificPatterns> GetAllShipPatterns()
    {
        return Ships.Values.ToList();
    }

    public void RemoveShip(string shipKey)
    {
        if (Ships.Remove(shipKey))
        {
            if (CurrentShipKey == shipKey)
            {
                CurrentShipKey = null;
            }
            LastUpdated = DateTime.UtcNow;
        }
    }

    public int GetTotalCustomPatterns()
    {
        return Ships.Values.Sum(ship => ship.EventPatterns.Count);
    }
}

// Ship classification for pattern suggestions
public static class ShipClassifications
{
    public static readonly Dictionary<string, ShipClass> ShipClasses = new()
    {
        // Small Ships
        { "sidewinder", new ShipClass("Small Fighter", ShipSize.Small, ShipRole.Combat) },
        { "eagle", new ShipClass("Small Fighter", ShipSize.Small, ShipRole.Combat) },
        { "hauler", new ShipClass("Light Freighter", ShipSize.Small, ShipRole.Transport) },
        { "adder", new ShipClass("Light Multipurpose", ShipSize.Small, ShipRole.Multipurpose) },
        { "viper", new ShipClass("Small Fighter", ShipSize.Small, ShipRole.Combat) },
        { "viper_mkii", new ShipClass("Small Fighter", ShipSize.Small, ShipRole.Combat) },
        { "viper_mkiv", new ShipClass("Small Fighter", ShipSize.Small, ShipRole.Combat) },
        { "cobra_mkiii", new ShipClass("Small Multipurpose", ShipSize.Small, ShipRole.Multipurpose) },
        { "cobra_mkiv", new ShipClass("Small Multipurpose", ShipSize.Small, ShipRole.Multipurpose) },
        { "type6", new ShipClass("Medium Freighter", ShipSize.Medium, ShipRole.Transport) },
        { "dolphin", new ShipClass("Small Luxury", ShipSize.Small, ShipRole.Exploration) },
        { "vulture", new ShipClass("Medium Fighter", ShipSize.Medium, ShipRole.Combat) },

        // Medium Ships
        { "asp", new ShipClass("Medium Explorer", ShipSize.Medium, ShipRole.Exploration) },
        { "asp_scout", new ShipClass("Medium Scout", ShipSize.Medium, ShipRole.Exploration) },
        { "diamondback", new ShipClass("Medium Explorer", ShipSize.Medium, ShipRole.Exploration) },
        { "diamondbackxl", new ShipClass("Medium Explorer", ShipSize.Medium, ShipRole.Exploration) },
        { "empire_courier", new ShipClass("Medium Fighter", ShipSize.Medium, ShipRole.Combat) },
        { "imperial_clipper", new ShipClass("Large Multipurpose", ShipSize.Large, ShipRole.Multipurpose) },
        { "federation_dropship", new ShipClass("Medium Combat", ShipSize.Medium, ShipRole.Combat) },
        { "federation_assault_ship", new ShipClass("Medium Assault", ShipSize.Medium, ShipRole.Combat) },
        { "federation_gunship", new ShipClass("Medium Gunship", ShipSize.Medium, ShipRole.Combat) },
        { "krait_mkii", new ShipClass("Medium Multipurpose", ShipSize.Medium, ShipRole.Multipurpose) },
        { "krait_light", new ShipClass("Medium Explorer", ShipSize.Medium, ShipRole.Exploration) },
        { "python", new ShipClass("Medium Multipurpose", ShipSize.Medium, ShipRole.Multipurpose) },
        { "type7", new ShipClass("Large Freighter", ShipSize.Large, ShipRole.Transport) },
        { "orca", new ShipClass("Large Luxury", ShipSize.Large, ShipRole.Exploration) },

        // Large Ships
        { "anaconda", new ShipClass("Large Multipurpose", ShipSize.Large, ShipRole.Multipurpose) },
        { "federation_corvette", new ShipClass("Large Combat", ShipSize.Large, ShipRole.Combat) },
        { "cutter", new ShipClass("Large Multipurpose", ShipSize.Large, ShipRole.Multipurpose) },
        { "type9", new ShipClass("Large Freighter", ShipSize.Large, ShipRole.Transport) },
        { "type10", new ShipClass("Large Combat Freighter", ShipSize.Large, ShipRole.Combat) },
        { "beluga", new ShipClass("Large Luxury", ShipSize.Large, ShipRole.Exploration) },
        { "chieftain", new ShipClass("Medium Combat", ShipSize.Medium, ShipRole.Combat) },
        { "challenger", new ShipClass("Medium Combat", ShipSize.Medium, ShipRole.Combat) },
        { "crusader", new ShipClass("Medium Combat", ShipSize.Medium, ShipRole.Combat) },
        { "mamba", new ShipClass("Medium Racing", ShipSize.Medium, ShipRole.Combat) },

        // Special/Unique Ships
        { "ferdelance", new ShipClass("Medium Racing Fighter", ShipSize.Medium, ShipRole.Combat) },
        { "independant_trader", new ShipClass("Medium Independent", ShipSize.Medium, ShipRole.Transport) }
    };

    public static ShipClass GetShipClass(string shipType)
    {
        var normalizedShipType = shipType.ToLowerInvariant().Replace(" ", "_");
        return ShipClasses.TryGetValue(normalizedShipType, out var shipClass) 
            ? shipClass 
            : new ShipClass("Unknown", ShipSize.Medium, ShipRole.Multipurpose);
    }

    public static PatternRecommendations GetPatternRecommendations(string shipType)
    {
        var shipClass = GetShipClass(shipType);
        
        return new PatternRecommendations
        {
            ShipSize = shipClass.Size,
            ShipRole = shipClass.Role,
            RecommendedDurationMultiplier = GetDurationMultiplier(shipClass.Size),
            RecommendedIntensityMultiplier = GetIntensityMultiplier(shipClass.Size),
            RecommendedFrequencyAdjustment = GetFrequencyAdjustment(shipClass.Size),
            SuggestedPatterns = GetSuggestedPatterns(shipClass)
        };
    }

    private static float GetDurationMultiplier(ShipSize size)
    {
        return size switch
        {
            ShipSize.Small => 0.7f,    // Faster, more agile ships - shorter patterns
            ShipSize.Medium => 1.0f,   // Baseline
            ShipSize.Large => 1.4f,    // Larger ships - longer, more substantial patterns
            _ => 1.0f
        };
    }

    private static float GetIntensityMultiplier(ShipSize size)
    {
        return size switch
        {
            ShipSize.Small => 0.8f,    // Lighter ships - less intense
            ShipSize.Medium => 1.0f,   // Baseline
            ShipSize.Large => 1.2f,    // Heavy ships - more intense
            _ => 1.0f
        };
    }

    private static int GetFrequencyAdjustment(ShipSize size)
    {
        return size switch
        {
            ShipSize.Small => +5,      // Higher frequency for small ships
            ShipSize.Medium => 0,      // Baseline frequency
            ShipSize.Large => -5,      // Lower frequency for large ships
            _ => 0
        };
    }

    private static Dictionary<string, string> GetSuggestedPatterns(ShipClass shipClass)
    {
        var suggestions = new Dictionary<string, string>();

        switch (shipClass.Role)
        {
            case ShipRole.Combat:
                suggestions["FSDJump"] = "Aggressive buildup with sharp end";
                suggestions["HullDamage"] = "Intense sharp pulses";
                suggestions["ShieldDown"] = "Dramatic warning burst";
                break;
                
            case ShipRole.Exploration:
                suggestions["FSDJump"] = "Smooth, extended buildup";
                suggestions["JetConeBoost"] = "Sustained power rumble";
                suggestions["FuelScoop"] = "Gentle sustained vibration";
                break;
                
            case ShipRole.Transport:
                suggestions["FSDJump"] = "Heavy, powerful buildup";
                suggestions["Docked"] = "Substantial docking thud";
                suggestions["CargoScoop"] = "Mechanical operation feel";
                break;
                
            case ShipRole.Multipurpose:
            default:
                suggestions["FSDJump"] = "Balanced buildup and release";
                suggestions["Docked"] = "Standard impact pattern";
                suggestions["HullDamage"] = "Proportional damage feedback";
                break;
        }

        return suggestions;
    }
}

public class ShipClass
{
    public string Name { get; set; }
    public ShipSize Size { get; set; }
    public ShipRole Role { get; set; }

    public ShipClass(string name, ShipSize size, ShipRole role)
    {
        Name = name;
        Size = size;
        Role = role;
    }
}

public class PatternRecommendations
{
    public ShipSize ShipSize { get; set; }
    public ShipRole ShipRole { get; set; }
    public float RecommendedDurationMultiplier { get; set; }
    public float RecommendedIntensityMultiplier { get; set; }
    public int RecommendedFrequencyAdjustment { get; set; }
    public Dictionary<string, string> SuggestedPatterns { get; set; } = new();
}

public enum ShipSize
{
    Small,
    Medium,
    Large
}

public enum ShipRole
{
    Combat,
    Exploration,
    Transport,
    Multipurpose
}