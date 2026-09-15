using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Collections.Concurrent;
using EDButtkicker.Configuration;
using EDButtkicker.Models;

namespace EDButtkicker.Services;

/// <summary>What happened to a single mapping edit, and why when it did not happen.</summary>
public enum EventMappingChangeStatus
{
    /// <summary>The live mappings hold the edit and it reached the file.</summary>
    Applied,

    /// <summary>Nothing is mapped to that event, so there was nothing to change.</summary>
    NotFound,

    /// <summary>Something is already mapped to that event.</summary>
    Conflict,

    /// <summary>The edit could not be written, so it was rolled back out of the live mappings.</summary>
    NotPersisted
}

/// <summary>
/// The outcome of one mapping edit. <see cref="Error"/> is a sentence safe to hand to a caller -
/// it never carries a path or exception text.
/// </summary>
public readonly record struct EventMappingChange(EventMappingChangeStatus Status, string? Error)
{
    public bool IsApplied => Status == EventMappingChangeStatus.Applied;

    public static EventMappingChange Applied() => new(EventMappingChangeStatus.Applied, null);

    public static EventMappingChange NotFound(string error) =>
        new(EventMappingChangeStatus.NotFound, error);

    public static EventMappingChange Conflict(string error) =>
        new(EventMappingChangeStatus.Conflict, error);

    public static EventMappingChange NotPersisted(string error) =>
        new(EventMappingChangeStatus.NotPersisted, error);
}

public class EventMappingService : IJournalEventAudioSink, IEventPatternSource
{
    private readonly ILogger<EventMappingService> _logger;
    private readonly AudioEngineService _audioEngine;
    private readonly PatternSequencer _patternSequencer;
    private readonly ContextualIntelligenceService _contextualIntelligence;

    /// <summary>
    /// Null wherever no speech engine exists - the service is only registered on Windows - so the
    /// pipeline asks for an announcement without ever asking what platform it is on.
    /// </summary>
    private readonly IVoiceFeedback? _voiceFeedback;

    private EventMappingsConfig _eventMappings;
    private readonly EventRateLimiter _rateLimiter;
    private readonly ConcurrentDictionary<string, int> _eventCounts = new();

    /// <summary>
    /// Serializes the read-modify-write of a mapping edit. Requests arrive on thread pool threads,
    /// so two concurrent edits must not each start from the same mappings and lose one another.
    /// </summary>
    private readonly object _editLock = new();

    public EventMappingService(
        ILogger<EventMappingService> logger,
        AudioEngineService audioEngine,
        PatternSequencer patternSequencer,
        ContextualIntelligenceService contextualIntelligence,
        UserSettingsService userSettings,
        TimeProvider timeProvider,
        IVoiceFeedback? voiceFeedback = null)
    {
        _logger = logger;
        _audioEngine = audioEngine;
        _patternSequencer = patternSequencer;
        _contextualIntelligence = contextualIntelligence;
        _voiceFeedback = voiceFeedback;
        _rateLimiter = new EventRateLimiter(timeProvider);
        MappingsFilePath = Path.Combine(userSettings.SettingsDirectory, "event-mappings.json");
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

            AnnounceEvent(eventType, pattern, journalEvent);

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

    /// <summary>
    /// Speaks one line for this event, if there is a voice engine and something to say. Contextual
    /// intelligence gets first refusal - it knows the situation the event arrived in, and returns
    /// null both when it has nothing to add and when contextual voice is switched off - and the
    /// pattern's own message is the fallback.
    ///
    /// Deliberately not awaited and deliberately not one of the feedback tasks: a spoken line runs
    /// for seconds, while the haptic hit it accompanies is measured in milliseconds. Joining them
    /// would hold the event pipeline open for the whole announcement and delay the next event
    /// behind it.
    /// </summary>
    private void AnnounceEvent(string eventType, HapticPattern pattern, JournalEvent journalEvent)
    {
        var voice = _voiceFeedback;
        if (voice is not { IsRunning: true }) return;

        var message = _contextualIntelligence.GetContextualVoiceMessage(eventType, journalEvent);

        if (string.IsNullOrWhiteSpace(message))
        {
            if (!pattern.EnableVoiceAnnouncement || string.IsNullOrWhiteSpace(pattern.VoiceMessage))
                return;

            message = pattern.VoiceMessage;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await voice.AnnounceAsync(message, journalEvent);
            }
            catch (Exception ex)
            {
                // Nothing awaits this task, so an escaped exception would otherwise be unobserved.
                _logger.LogError(ex, "Error announcing voice feedback for {EventType}", eventType);
            }
        });
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

    /// <summary>
    /// Where an edit made through the API is written, and where <see cref="LoadSavedEventMappings"/>
    /// reads from. It sits beside the other per-user state, so a test that redirects the settings
    /// directory redirects this too.
    /// </summary>
    public string MappingsFilePath { get; }

    /// <summary>
    /// Loads the edits saved by previous runs, if there are any. Called once at startup: an event
    /// the user deleted stays deleted, and one they added is there again. A saved file is the whole
    /// mapping set, so it does replace the built-in catalogue rather than merging into it.
    /// </summary>
    public void LoadSavedEventMappings()
    {
        if (!File.Exists(MappingsFilePath))
        {
            _logger.LogDebug("No saved event mappings at {Path}; using the built-in catalogue", MappingsFilePath);
            return;
        }

        LoadEventMappings(MappingsFilePath);
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
        TryWriteEventMappings(configPath);
    }

    /// <summary>
    /// Writes the live mappings, returning the reason when the write did not happen. The reason is
    /// the caller-safe sentence; the exception and the path go to the log.
    /// </summary>
    private string? TryWriteEventMappings(string configPath)
    {
        try
        {
            var json = JsonSerializer.Serialize(_eventMappings, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true
            });

            var directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(configPath, json);
            _logger.LogInformation("Saved event mappings to {Path}", configPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving event mappings to {Path}", configPath);
            return "The event mappings could not be written to disk";
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

    /// <summary>The mapping stored for an event, or null when nothing is mapped to it.</summary>
    public EventMapping? GetEventMapping(string eventType) =>
        _eventMappings.EventMappings.TryGetValue(eventType, out var mapping) ? mapping : null;

    /// <summary>Every stored mapping, as the API and the web UI list them.</summary>
    public IReadOnlyDictionary<string, EventMapping> GetEventMappings() => _eventMappings.EventMappings;

    /// <summary>
    /// The pattern mapped to an event, for callers that only need what it says rather than whether
    /// it is enabled - see <see cref="IEventPatternSource"/>. Reads the live mappings, so a mapping
    /// the user has edited is the one the voice reads from.
    /// </summary>
    public HapticPattern? GetPattern(string eventType) => GetEventMapping(eventType)?.Pattern;

    /// <summary>
    /// Maps a pattern to an event that has none. Existing events are the update path, so this
    /// refuses rather than overwriting one.
    /// </summary>
    public EventMappingChange AddEventMapping(EventMapping mapping)
    {
        lock (_editLock)
        {
            if (_eventMappings.EventMappings.ContainsKey(mapping.EventType))
            {
                return EventMappingChange.Conflict(
                    $"A pattern is already mapped to event '{mapping.EventType}'");
            }

            var edited = CopyMappings();
            edited[mapping.EventType] = mapping;

            return ApplyAndPersist(edited);
        }
    }

    /// <summary>
    /// Replaces the pattern mapped to an existing event. <paramref name="enabled"/> left null keeps
    /// whatever the mapping already had.
    /// </summary>
    public EventMappingChange UpdateEventMapping(string eventType, HapticPattern pattern, bool? enabled = null)
    {
        lock (_editLock)
        {
            if (!_eventMappings.EventMappings.TryGetValue(eventType, out var existing))
            {
                return EventMappingChange.NotFound($"No pattern is mapped to event '{eventType}'");
            }

            var edited = CopyMappings();
            edited[eventType] = new EventMapping
            {
                EventType = eventType,
                Pattern = pattern,
                Enabled = enabled ?? existing.Enabled
            };

            return ApplyAndPersist(edited);
        }
    }

    /// <summary>Unmaps an event. Removing what was never there is a failure, not a no-op success.</summary>
    public EventMappingChange RemoveEventMapping(string eventType)
    {
        lock (_editLock)
        {
            if (!_eventMappings.EventMappings.ContainsKey(eventType))
            {
                return EventMappingChange.NotFound($"No pattern is mapped to event '{eventType}'");
            }

            var edited = CopyMappings();
            edited.Remove(eventType);

            return ApplyAndPersist(edited);
        }
    }

    /// <summary>
    /// Copy on write: playback reads the mappings on its own threads, so an edit builds a new
    /// dictionary rather than mutating the one being read.
    /// </summary>
    private Dictionary<string, EventMapping> CopyMappings() => new(_eventMappings.EventMappings);

    /// <summary>
    /// Publishes the edited mappings and writes them out. A failed write puts the previous mappings
    /// back, so a caller is never told about an edit that neither the process nor the file kept.
    /// </summary>
    private EventMappingChange ApplyAndPersist(Dictionary<string, EventMapping> edited)
    {
        var previous = _eventMappings;
        UpdateEventMappings(new EventMappingsConfig { EventMappings = edited });

        var error = TryWriteEventMappings(MappingsFilePath);
        if (error != null)
        {
            UpdateEventMappings(previous);
            return EventMappingChange.NotPersisted(error);
        }

        return EventMappingChange.Applied();
    }
}
