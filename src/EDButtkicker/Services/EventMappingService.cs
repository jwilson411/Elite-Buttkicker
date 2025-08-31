using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Collections.Concurrent;
using EDButtkicker.Configuration;
using EDButtkicker.Models;

namespace EDButtkicker.Services;

public class EventMappingService
{
    private readonly ILogger<EventMappingService> _logger;
    private readonly AudioEngineService _audioEngine;
    private readonly PatternSequencer _patternSequencer;
    private readonly ContextualIntelligenceService _contextualIntelligence;
    private EventMappingsConfig _eventMappings;
    private readonly ConcurrentDictionary<string, DateTime> _lastEventTimes = new();
    private readonly ConcurrentDictionary<string, int> _eventCounts = new();

    public EventMappingService(
        ILogger<EventMappingService> logger,
        AudioEngineService audioEngine,
        PatternSequencer patternSequencer,
        ContextualIntelligenceService contextualIntelligence)
    {
        _logger = logger;
        _audioEngine = audioEngine;
        _patternSequencer = patternSequencer;
        _contextualIntelligence = contextualIntelligence;
        _eventMappings = EventMappingsConfig.GetDefault();
        
        // Initialize services
        _audioEngine.Initialize();
        _patternSequencer.LoadPatterns(_eventMappings);
        
        _logger.LogInformation("Event Mapping Service initialized with {Count} default patterns", 
            _eventMappings.EventMappings.Count);
    }

    public async Task ProcessEvent(JournalEvent journalEvent)
    {
        try
        {
            if (string.IsNullOrEmpty(journalEvent.Event))
                return;

            var eventType = journalEvent.Event;
            
            // Process for contextual intelligence first (even for unmapped events)
            _contextualIntelligence.ProcessEvent(journalEvent);
            
            // Check if we have a mapping for this event
            if (!_eventMappings.EventMappings.TryGetValue(eventType, out var mapping))
            {
                // Log unmapped events occasionally to avoid spam
                LogUnmappedEvent(eventType);
                return;
            }

            if (!mapping.Enabled)
            {
                _logger.LogDebug("Event mapping disabled for: {EventType}", eventType);
                return;
            }

            // Check for rate limiting to prevent audio spam
            if (ShouldRateLimit(eventType))
            {
                _logger.LogDebug("Rate limiting event: {EventType}", eventType);
                return;
            }

            _logger.LogInformation("Processing mapped event: {EventType}", eventType);
            
            // Track event timing (thread-safe)
            _lastEventTimes[eventType] = DateTime.UtcNow;
            _eventCounts.AddOrUpdate(eventType, 1, (key, value) => value + 1);

            // Apply any event-specific modifications to the pattern
            var basePattern = CreatePatternForEvent(mapping.Pattern, journalEvent);
            
            // Apply contextual intelligence adjustments
            var pattern = _contextualIntelligence.GetContextuallyAdjustedPattern(basePattern, journalEvent);

            // Create tasks for parallel execution
            var tasks = new List<Task>();

            // Haptic feedback - choose appropriate execution method
            if (pattern.Conditions.Any())
            {
                tasks.Add(_patternSequencer.ExecuteConditionalPattern(pattern, journalEvent));
            }
            else if (pattern.Pattern == PatternType.Sequence || pattern.ChainedPatterns.Any())
            {
                tasks.Add(_patternSequencer.ExecutePatternSequence(pattern, journalEvent));
            }
            else
            {
                tasks.Add(_audioEngine.PlayHapticPattern(pattern, journalEvent));
            }

            // Voice feedback has been removed for better user experience

            // Execute all feedback simultaneously
            await Task.WhenAll(tasks);

            _logger.LogDebug("Triggered feedback for {EventType}: {PatternName}", 
                eventType, pattern.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing journal event: {EventType}", journalEvent.Event);
        }
    }

    private HapticPattern CreatePatternForEvent(HapticPattern basePattern, JournalEvent journalEvent)
    {
        // Create a copy of the base pattern
        var pattern = new HapticPattern
        {
            Name = basePattern.Name,
            Pattern = basePattern.Pattern,
            Frequency = basePattern.Frequency,
            Duration = basePattern.Duration,
            Intensity = basePattern.Intensity,
            FadeIn = basePattern.FadeIn,
            FadeOut = basePattern.FadeOut,
            IntensityFromDamage = basePattern.IntensityFromDamage,
            MaxIntensity = basePattern.MaxIntensity,
            MinIntensity = basePattern.MinIntensity
        };

        // Apply event-specific modifications
        ApplyEventSpecificModifications(pattern, journalEvent);

        return pattern;
    }

    private void ApplyEventSpecificModifications(HapticPattern pattern, JournalEvent journalEvent)
    {
        switch (journalEvent.Event)
        {
            case "FSDJump":
                ApplyFSDJumpModifications(pattern, journalEvent);
                break;
                
            case "HullDamage":
                ApplyHullDamageModifications(pattern, journalEvent);
                break;
                
            case "Docked":
            case "Undocked":
                ApplyDockingModifications(pattern, journalEvent);
                break;
                
            case "ShipTargeted":
                ApplyTargetingModifications(pattern, journalEvent);
                break;
                
            case "FighterDestroyed":
            case "ShipDestroyed":
                ApplyExplosionModifications(pattern, journalEvent);
                break;
                
            case "Touchdown":
            case "Liftoff":
                ApplyPlanetaryModifications(pattern, journalEvent);
                break;
                
            case "HeatWarning":
            case "HeatDamage":
                ApplyHeatModifications(pattern, journalEvent);
                break;
                
            case "FuelScoop":
                ApplyFuelScoopModifications(pattern, journalEvent);
                break;
                
            case "UnderAttack":
                ApplyUnderAttackModifications(pattern, journalEvent);
                break;
                
            case "LaunchFighter":
            case "DockFighter":
                ApplyFighterModifications(pattern, journalEvent);
                break;
                
            case "JetConeBoost":
                ApplyNeutronBoostModifications(pattern, journalEvent);
                break;
                
            case "Interdicted":
            case "Interdiction":
                ApplyInterdictionModifications(pattern, journalEvent);
                break;
                
            case "ShieldDown":
            case "ShieldsUp":
                ApplyShieldModifications(pattern, journalEvent);
                break;
        }
    }

    private void ApplyFSDJumpModifications(HapticPattern pattern, JournalEvent journalEvent)
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
                
                _logger.LogDebug("FSD Jump distance: {Distance} Ly, intensity multiplier: {Multiplier}", 
                    jumpDist, distanceMultiplier);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error parsing jump distance");
            }
        }
    }

    private void ApplyHullDamageModifications(HapticPattern pattern, JournalEvent journalEvent)
    {
        if (journalEvent.Health.HasValue)
        {
            // Scale frequency based on remaining health (lower health = lower frequency)
            var healthPercent = journalEvent.Health.Value;
            var freqMultiplier = 0.7 + (healthPercent * 0.3); // 0.7 to 1.0 range
            pattern.Frequency = (int)(pattern.Frequency * freqMultiplier);
            
            _logger.LogDebug("Hull damage - Health: {Health}%, frequency: {Frequency}Hz", 
                healthPercent * 100, pattern.Frequency);
        }
    }

    private void ApplyDockingModifications(HapticPattern pattern, JournalEvent journalEvent)
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

    private void ApplyTargetingModifications(HapticPattern pattern, JournalEvent journalEvent)
    {
        // Quick, subtle pulse for targeting
        // Could differentiate between ship types if target info is available
        if (!string.IsNullOrEmpty(journalEvent.Target))
        {
            _logger.LogDebug("Target acquired: {Target}", journalEvent.Target);
        }
    }

    private void ApplyExplosionModifications(HapticPattern pattern, JournalEvent journalEvent)
    {
        // More intense explosion for larger ships
        pattern.Intensity = Math.Min(100, (int)(pattern.Intensity * 1.1));
    }

    private void ApplyPlanetaryModifications(HapticPattern pattern, JournalEvent journalEvent)
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
            _logger.LogDebug("Planetary event on body: {Body}", journalEvent.AdditionalData["Body"]);
        }
    }

    private void ApplyHeatModifications(HapticPattern pattern, JournalEvent journalEvent)
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
                
                _logger.LogDebug("Heat event - Level: {Heat}%, intensity: {Intensity}%", 
                    heatLevel * 100, pattern.Intensity);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error parsing heat level");
            }
        }
    }

    private void ApplyFuelScoopModifications(HapticPattern pattern, JournalEvent journalEvent)
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
                
                _logger.LogDebug("Fuel scoop rate: {Rate} kg/s, intensity multiplier: {Multiplier}", 
                    scoopRate, rateMultiplier);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error parsing scoop rate");
            }
        }
    }

    private void ApplyUnderAttackModifications(HapticPattern pattern, JournalEvent journalEvent)
    {
        // Intense, immediate feedback for combat
        pattern.Intensity = Math.Min(100, pattern.Intensity + 10);
        
        // Check for target information if available
        if (journalEvent.AdditionalData?.ContainsKey("Target") == true)
        {
            var target = journalEvent.AdditionalData["Target"]?.ToString();
            if (!string.IsNullOrEmpty(target))
            {
                _logger.LogDebug("Under attack by: {Target}", target);
                
                // More intense for larger attackers
                if (target.ToLower().Contains("anaconda") || target.ToLower().Contains("corvette"))
                {
                    pattern.Intensity = Math.Min(100, (int)(pattern.Intensity * 1.2));
                }
            }
        }
    }

    private void ApplyFighterModifications(HapticPattern pattern, JournalEvent journalEvent)
    {
        // Fighter operations are generally lighter events
        pattern.Intensity = (int)(pattern.Intensity * 0.9);
        
        if (journalEvent.AdditionalData?.ContainsKey("ID") == true)
        {
            var fighterId = journalEvent.AdditionalData["ID"]?.ToString();
            _logger.LogDebug("Fighter operation - ID: {FighterID}", fighterId);
        }
    }

    private void ApplyNeutronBoostModifications(HapticPattern pattern, JournalEvent journalEvent)
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
                
                _logger.LogDebug("Neutron boost: {Boost}x FSD range", boostValue);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error parsing boost value");
            }
        }
    }

    private void ApplyInterdictionModifications(HapticPattern pattern, JournalEvent journalEvent)
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
            _logger.LogDebug("Interdiction {Result}", success ? "successful" : "failed");
            
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
            _logger.LogDebug("Interdicted by: {Interdictor}", interdictor);
        }
    }

    private void ApplyShieldModifications(HapticPattern pattern, JournalEvent journalEvent)
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

    private bool ShouldRateLimit(string eventType)
    {
        // Define rate limits for different event types
        var rateLimits = new Dictionary<string, TimeSpan>
        {
            ["HullDamage"] = TimeSpan.FromMilliseconds(500), // Max once per 500ms
            ["ShipTargeted"] = TimeSpan.FromMilliseconds(1000), // Max once per second
            ["FuelScoop"] = TimeSpan.FromSeconds(2), // Max once per 2 seconds
            ["HeatWarning"] = TimeSpan.FromSeconds(1), // Max once per second
            ["HeatDamage"] = TimeSpan.FromMilliseconds(800), // Max once per 800ms
            ["UnderAttack"] = TimeSpan.FromMilliseconds(300), // Max once per 300ms
            ["Touchdown"] = TimeSpan.FromSeconds(3), // Max once per 3 seconds (prevent spam on bouncy landings)
            ["Liftoff"] = TimeSpan.FromSeconds(3), // Max once per 3 seconds
            ["ShieldDown"] = TimeSpan.FromSeconds(2), // Max once per 2 seconds
            ["ShieldsUp"] = TimeSpan.FromSeconds(2) // Max once per 2 seconds
        };

        if (!rateLimits.TryGetValue(eventType, out var minInterval))
            return false; // No rate limiting for this event type

        if (!_lastEventTimes.TryGetValue(eventType, out var lastTime))
            return false; // First occurrence

        return DateTime.UtcNow - lastTime < minInterval;
    }

    private void LogUnmappedEvent(string eventType)
    {
        // Only log each unmapped event type once per session to avoid spam
        const string unmappedKey = "UNMAPPED_";
        var logKey = unmappedKey + eventType;
        
        // Thread-safe way to add if not exists
        if (_eventCounts.TryAdd(logKey, 1))
        {
            _logger.LogDebug("No mapping found for event type: {EventType}", eventType);
        }
    }

    public void LoadEventMappings(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                _logger.LogWarning("Event mappings file not found: {Path}", configPath);
                return;
            }

            var json = File.ReadAllText(configPath);
            var mappings = JsonSerializer.Deserialize<EventMappingsConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (mappings != null)
            {
                _eventMappings = mappings;
                _logger.LogInformation("Loaded {Count} event mappings from {Path}", 
                    mappings.EventMappings.Count, configPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading event mappings from {Path}", configPath);
        }
    }

    public void SaveEventMappings(string configPath)
    {
        try
        {
            var json = JsonSerializer.Serialize(_eventMappings, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true
            });

            File.WriteAllText(configPath, json);
            _logger.LogInformation("Saved event mappings to {Path}", configPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving event mappings to {Path}", configPath);
        }
    }

    public Dictionary<string, int> GetEventStatistics()
    {
        return new Dictionary<string, int>(_eventCounts);
    }

    public void ResetStatistics()
    {
        _eventCounts.Clear();
        _lastEventTimes.Clear();
        _logger.LogInformation("Event statistics reset");
    }

}