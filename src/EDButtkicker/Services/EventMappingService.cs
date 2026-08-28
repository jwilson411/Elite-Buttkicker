using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Collections.Concurrent;
using EDButtkicker.Configuration;
using EDButtkicker.Models;

namespace EDButtkicker.Services;

public class EventMappingService : IJournalEventAudioSink
{
    private readonly ILogger<EventMappingService> _logger;
    private readonly AudioEngineService _audioEngine;
    private readonly PatternSequencer _patternSequencer;
    private readonly ContextualIntelligenceService _contextualIntelligence;
    private EventMappingsConfig _eventMappings;
    private readonly EventRateLimiter _rateLimiter;
    private readonly ConcurrentDictionary<string, int> _eventCounts = new();

    public EventMappingService(
        ILogger<EventMappingService> logger,
        AudioEngineService audioEngine,
        PatternSequencer patternSequencer,
        ContextualIntelligenceService contextualIntelligence,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _audioEngine = audioEngine;
        _patternSequencer = patternSequencer;
        _contextualIntelligence = contextualIntelligence;
        _rateLimiter = new EventRateLimiter(timeProvider);
        _eventMappings = EventMappingsConfig.GetDefault();

        // No audio device work here: the engine opens itself on first playback so that building
        // the service graph never touches hardware.
        _patternSequencer.LoadPatterns(_eventMappings);
        
        _logger.LogInformation("Event Mapping Service initialized with {Count} default patterns", 
            _eventMappings.EventMappings.Count);
    }

    public Task ProcessEvent(JournalEvent journalEvent) => ProcessEvent(journalEvent, null);

    /// <summary>
    /// Processes one event. <paramref name="preferredPattern"/> - typically the active
    /// ship-specific pattern - is used as the base pattern instead of the default mapping.
    /// </summary>
    public async Task ProcessEvent(JournalEvent journalEvent, HapticPattern? preferredPattern)
    {
        try
        {
            if (string.IsNullOrEmpty(journalEvent.Event))
                return;

            var eventType = journalEvent.Event;

            // Process for contextual intelligence first (even for unmapped events)
            _contextualIntelligence.ProcessEvent(journalEvent);

            // Check if we have a mapping for this event
            var hasMapping = _eventMappings.EventMappings.TryGetValue(eventType, out var mapping);
            if (!hasMapping && preferredPattern == null)
            {
                // Log unmapped events occasionally to avoid spam
                LogUnmappedEvent(eventType);
                return;
            }

            if (hasMapping && !mapping!.Enabled)
            {
                _logger.LogDebug("Event mapping disabled for: {EventType}", eventType);
                return;
            }

            // Check for rate limiting to prevent audio spam. Acquiring also records the acceptance,
            // so the next occurrence inside the window is refused.
            if (!_rateLimiter.TryAcquire(eventType))
            {
                _logger.LogDebug("Rate limiting event: {EventType}", eventType);
                return;
            }

            _logger.LogInformation("Processing mapped event: {EventType}", eventType);

            _eventCounts.AddOrUpdate(eventType, 1, (key, value) => value + 1);

            // Apply any event-specific modifications to the pattern
            var sourcePattern = preferredPattern ?? mapping!.Pattern;
            var basePattern = CreatePatternForEvent(sourcePattern, journalEvent);
            
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
        // Deep clone first, then adjust the clone only - the stored mapping keeps its defaults.
        return EventPatternFactory.CreatePatternForEvent(basePattern, journalEvent, _logger);
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
        _rateLimiter.Reset();
        _logger.LogInformation("Event statistics reset");
    }

    private bool ShouldAnnounceEvent(string eventType)
    {
        // Define which events should have voice announcements
        var announcementEvents = new HashSet<string>
        {
            "FSDJump", "Docked", "Undocked", "ShieldDown", "ShieldsUp",
            "UnderAttack", "HeatWarning", "HeatDamage", "Interdicted",
            "JetConeBoost", "Touchdown", "Liftoff"
        };

        return announcementEvents.Contains(eventType);
    }

    public HapticPattern? GetDefaultPatternForEvent(string eventType)
    {
        if (_eventMappings.EventMappings.TryGetValue(eventType, out var mapping))
        {
            return mapping.Pattern;
        }
        
        _logger.LogDebug("No default pattern found for event: {EventType}", eventType);
        return null;
    }

    public Dictionary<string, HapticPattern> GetAllDefaultPatterns()
    {
        return _eventMappings.EventMappings.ToDictionary(
            kvp => kvp.Key, 
            kvp => kvp.Value.Pattern
        );
    }

    public void UpdateEventMappings(EventMappingsConfig newMappings)
    {
        _eventMappings = newMappings;
        _patternSequencer.LoadPatterns(_eventMappings);
        _logger.LogInformation("Event mappings updated with {Count} patterns", newMappings.EventMappings.Count);
    }
}
