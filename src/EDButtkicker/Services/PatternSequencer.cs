using Microsoft.Extensions.Logging;
using EDButtkicker.Models;
using EDButtkicker.Configuration;

namespace EDButtkicker.Services;

public class PatternSequencer
{
    private readonly ILogger<PatternSequencer> _logger;
    private readonly AudioEngineService _audioEngine;
    private readonly ContextualIntelligenceService? _contextualIntelligence;
    private readonly Dictionary<string, HapticPattern> _availablePatterns = new();

    public PatternSequencer(
        ILogger<PatternSequencer> logger, 
        AudioEngineService audioEngine,
        ContextualIntelligenceService? contextualIntelligence = null)
    {
        _logger = logger;
        _audioEngine = audioEngine;
        _contextualIntelligence = contextualIntelligence;
    }

    public void LoadPatterns(EventMappingsConfig mappingsConfig)
    {
        _availablePatterns.Clear();
        
        foreach (var mapping in mappingsConfig.EventMappings.Values)
        {
            _availablePatterns[mapping.Pattern.Name] = mapping.Pattern;
        }
        
        _logger.LogInformation("Loaded {Count} patterns for sequencing", _availablePatterns.Count);
    }

    public async Task ExecutePatternSequence(HapticPattern rootPattern, JournalEvent? journalEvent = null)
    {
        try
        {
            if (rootPattern.Pattern == PatternType.Sequence && rootPattern.ChainedPatterns.Any())
            {
                _logger.LogDebug("Executing pattern sequence: {PatternName} -> [{ChainedPatterns}]", 
                    rootPattern.Name, string.Join(", ", rootPattern.ChainedPatterns));

                // Execute root pattern first
                var rootTask = _audioEngine.PlayHapticPattern(rootPattern, journalEvent);
                
                // Schedule chained patterns with delays
                var chainTasks = new List<Task>();
                int totalDelay = rootPattern.Duration;

                foreach (var chainedPatternName in rootPattern.ChainedPatterns)
                {
                    if (_availablePatterns.TryGetValue(chainedPatternName, out var chainedPattern))
                    {
                        var delay = totalDelay;
                        chainTasks.Add(Task.Run(async () =>
                        {
                            await Task.Delay(delay);
                            
                            // Apply contextual adjustments to chained patterns
                            var contextualPattern = _contextualIntelligence?.GetContextuallyAdjustedPattern(chainedPattern, journalEvent) ?? chainedPattern;
                            await _audioEngine.PlayHapticPattern(contextualPattern, journalEvent);
                        }));
                        
                        totalDelay += chainedPattern.Duration + 100; // 100ms gap between patterns
                        _logger.LogDebug("Scheduled pattern '{PatternName}' with {Delay}ms delay", 
                            chainedPatternName, delay);
                    }
                    else
                    {
                        _logger.LogWarning("Chained pattern not found: {PatternName}", chainedPatternName);
                    }
                }

                // Wait for all patterns to complete
                await Task.WhenAll(new[] { rootTask }.Concat(chainTasks));
            }
            else
            {
                // Single pattern execution
                await _audioEngine.PlayHapticPattern(rootPattern, journalEvent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing pattern sequence: {PatternName}", rootPattern.Name);
        }
    }

    public async Task ExecuteConditionalPattern(HapticPattern pattern, JournalEvent journalEvent)
    {
        try
        {
            if (!pattern.Conditions.Any())
            {
                await ExecutePatternSequence(pattern, journalEvent);
                return;
            }

            bool conditionsMet = EvaluateConditions(pattern.Conditions, journalEvent);
            
            if (conditionsMet)
            {
                _logger.LogDebug("Conditions met for pattern: {PatternName}", pattern.Name);
                await ExecutePatternSequence(pattern, journalEvent);
            }
            else
            {
                _logger.LogDebug("Conditions not met for pattern: {PatternName}", pattern.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing conditional pattern: {PatternName}", pattern.Name);
        }
    }

    private bool EvaluateConditions(Dictionary<string, object> conditions, JournalEvent journalEvent)
    {
        foreach (var condition in conditions)
        {
            if (!EvaluateCondition(condition.Key, condition.Value, journalEvent))
            {
                return false; // All conditions must be met (AND logic)
            }
        }
        
        return true;
    }

    private bool EvaluateCondition(string conditionType, object conditionValue, JournalEvent journalEvent)
    {
        try
        {
            return conditionType.ToLower() switch
            {
                "health_below" => EvaluateHealthBelow(conditionValue, journalEvent),
                "health_above" => EvaluateHealthAbove(conditionValue, journalEvent),
                "ship_type" => EvaluateShipType(conditionValue, journalEvent),
                "event_frequency" => EvaluateEventFrequency(conditionValue, journalEvent),
                "time_of_day" => EvaluateTimeOfDay(conditionValue),
                "session_duration" => EvaluateSessionDuration(conditionValue),
                "hull_damage_above" => EvaluateHullDamageAbove(conditionValue, journalEvent),
                "in_combat" => EvaluateInCombat(conditionValue, journalEvent),
                _ => true // Unknown conditions default to true
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error evaluating condition {ConditionType}", conditionType);
            return true; // Default to true on error
        }
    }

    private bool EvaluateHealthBelow(object threshold, JournalEvent journalEvent)
    {
        if (journalEvent.Health.HasValue && double.TryParse(threshold.ToString(), out double thresholdValue))
        {
            return journalEvent.Health.Value < thresholdValue;
        }
        return false;
    }

    private bool EvaluateHealthAbove(object threshold, JournalEvent journalEvent)
    {
        if (journalEvent.Health.HasValue && double.TryParse(threshold.ToString(), out double thresholdValue))
        {
            return journalEvent.Health.Value > thresholdValue;
        }
        return false;
    }

    private bool EvaluateShipType(object shipType, JournalEvent journalEvent)
    {
        if (string.IsNullOrEmpty(journalEvent.Ship) || shipType == null)
            return false;

        string expectedShip = shipType.ToString()!.ToLower();
        return journalEvent.Ship.ToLower().Contains(expectedShip);
    }

    private bool EvaluateEventFrequency(object frequency, JournalEvent journalEvent)
    {
        // This would need to track event frequency over time
        // For now, just return true as a placeholder
        return true;
    }

    private bool EvaluateTimeOfDay(object timeRange)
    {
        // Example: "06:00-18:00" for daytime only patterns
        if (timeRange?.ToString() is not string timeStr) return true;
        
        var parts = timeStr.Split('-');
        if (parts.Length != 2) return true;
        
        if (TimeOnly.TryParse(parts[0], out var startTime) && 
            TimeOnly.TryParse(parts[1], out var endTime))
        {
            var currentTime = TimeOnly.FromDateTime(DateTime.Now);
            return currentTime >= startTime && currentTime <= endTime;
        }
        
        return true;
    }

    private bool EvaluateSessionDuration(object duration)
    {
        // This would need to track session start time
        // For now, just return true as a placeholder
        return true;
    }

    private bool EvaluateHullDamageAbove(object threshold, JournalEvent journalEvent)
    {
        if (journalEvent.HullDamage.HasValue && double.TryParse(threshold.ToString(), out double thresholdValue))
        {
            return journalEvent.HullDamage.Value > thresholdValue;
        }
        return false;
    }

    private bool EvaluateInCombat(object expectedState, JournalEvent journalEvent)
    {
        if (!bool.TryParse(expectedState.ToString(), out bool expectCombat))
            return true;

        // This would need to track combat state across events
        // For now, check if the event suggests combat
        string[] combatEvents = { "UnderAttack", "HullDamage", "ShieldDown", "FighterDestroyed" };
        bool inCombat = combatEvents.Contains(journalEvent.Event);
        
        return expectCombat == inCombat;
    }
}