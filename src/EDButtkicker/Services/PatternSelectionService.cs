using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using EDButtkicker.Models;

namespace EDButtkicker.Services;

public class PatternSelectionService
{
    private readonly ILogger<PatternSelectionService> _logger;
    private readonly string _selectionsPath;
    private readonly JsonSerializerOptions _jsonOptions;
    
    private PatternSelections _selections = new();
    private readonly object _lock = new object();

    public event Action<PatternSelectionChangedEventArgs>? SelectionChanged;

    public PatternSelectionService(ILogger<PatternSelectionService> logger)
    {
        _logger = logger;
        
        // Store selections in user's AppData
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var settingsDir = Path.Combine(appDataPath, "EDButtkicker");
        Directory.CreateDirectory(settingsDir);
        
        _selectionsPath = Path.Combine(settingsDir, "pattern-selections.json");
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() }
        };
        
        _logger.LogDebug("PatternSelectionService initialized with path: {SelectionsPath}", _selectionsPath);
    }

    public async Task LoadSelectionsAsync()
    {
        try
        {
            if (!File.Exists(_selectionsPath))
            {
                _logger.LogInformation("No pattern selections file found, starting with defaults");
                return;
            }

            var json = await File.ReadAllTextAsync(_selectionsPath);
            var selections = JsonSerializer.Deserialize<PatternSelections>(json, _jsonOptions);
            
            if (selections != null)
            {
                lock (_lock)
                {
                    _selections = selections;
                }
                _logger.LogInformation("Loaded pattern selections for {SelectionCount} ship/event combinations", 
                    selections.GetTotalSelections());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading pattern selections, starting with defaults");
            lock (_lock)
            {
                _selections = new PatternSelections();
            }
        }
    }

    public async Task SaveSelectionsAsync()
    {
        try
        {
            PatternSelections selectionsToSave;
            lock (_lock)
            {
                selectionsToSave = _selections;
            }

            var json = JsonSerializer.Serialize(selectionsToSave, _jsonOptions);
            await File.WriteAllTextAsync(_selectionsPath, json);
            
            _logger.LogInformation("Saved pattern selections for {SelectionCount} ship/event combinations", 
                selectionsToSave.GetTotalSelections());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving pattern selections");
            throw;
        }
    }

    public void RegisterPatternSource(string shipType, string eventName, PatternSourceInfo sourceInfo)
    {
        lock (_lock)
        {
            var key = GetSelectionKey(shipType, eventName);
            
            if (!_selections.AvailablePatterns.ContainsKey(key))
            {
                _selections.AvailablePatterns[key] = new List<PatternSourceInfo>();
            }
            
            var existingSources = _selections.AvailablePatterns[key];
            
            // Remove any existing source with the same identifier
            existingSources.RemoveAll(s => s.SourceId == sourceInfo.SourceId);
            
            // Add the new/updated source
            existingSources.Add(sourceInfo);
            
            // If this is the first pattern for this ship/event, make it active by default
            if (!_selections.ActiveSelections.ContainsKey(key))
            {
                _selections.ActiveSelections[key] = sourceInfo.SourceId;
                _logger.LogInformation("Auto-selected first pattern for {ShipType}.{EventName}: {SourceName}", 
                    shipType, eventName, sourceInfo.SourceName);
            }
            else
            {
                // Check if the currently active selection still exists
                var activeSourceId = _selections.ActiveSelections[key];
                if (!existingSources.Any(s => s.SourceId == activeSourceId))
                {
                    // Active selection no longer exists, switch to the new one
                    _selections.ActiveSelections[key] = sourceInfo.SourceId;
                    _logger.LogInformation("Switched to new pattern for {ShipType}.{EventName} (previous no longer available): {SourceName}", 
                        shipType, eventName, sourceInfo.SourceName);
                }
            }
            
            _selections.LastUpdated = DateTime.UtcNow;
        }
    }

    public void SetActivePattern(string shipType, string eventName, string sourceId)
    {
        lock (_lock)
        {
            var key = GetSelectionKey(shipType, eventName);
            
            // Verify the source exists
            if (_selections.AvailablePatterns.TryGetValue(key, out var sources) &&
                sources.Any(s => s.SourceId == sourceId))
            {
                var previousSelection = _selections.ActiveSelections.GetValueOrDefault(key);
                _selections.ActiveSelections[key] = sourceId;
                _selections.LastUpdated = DateTime.UtcNow;
                
                var sourceInfo = sources.First(s => s.SourceId == sourceId);
                _logger.LogInformation("Pattern selection changed for {ShipType}.{EventName}: {SourceName}", 
                    shipType, eventName, sourceInfo.SourceName);
                
                // Notify subscribers
                SelectionChanged?.Invoke(new PatternSelectionChangedEventArgs
                {
                    ShipType = shipType,
                    EventName = eventName,
                    NewSourceId = sourceId,
                    PreviousSourceId = previousSelection,
                    SourceInfo = sourceInfo
                });
            }
            else
            {
                _logger.LogWarning("Attempted to select non-existent pattern source: {SourceId} for {ShipType}.{EventName}", 
                    sourceId, shipType, eventName);
            }
        }
    }

    public string? GetActivePatternSource(string shipType, string eventName)
    {
        lock (_lock)
        {
            var key = GetSelectionKey(shipType, eventName);
            return _selections.ActiveSelections.GetValueOrDefault(key);
        }
    }

    public PatternSourceInfo? GetActivePatternInfo(string shipType, string eventName)
    {
        lock (_lock)
        {
            var key = GetSelectionKey(shipType, eventName);
            var activeSourceId = _selections.ActiveSelections.GetValueOrDefault(key);
            
            if (activeSourceId != null && 
                _selections.AvailablePatterns.TryGetValue(key, out var sources))
            {
                return sources.FirstOrDefault(s => s.SourceId == activeSourceId);
            }
            
            return null;
        }
    }

    public List<PatternSourceInfo> GetAvailablePatterns(string shipType, string eventName)
    {
        lock (_lock)
        {
            var key = GetSelectionKey(shipType, eventName);
            return _selections.AvailablePatterns.GetValueOrDefault(key, new List<PatternSourceInfo>());
        }
    }

    public PatternConflictSummary GetConflicts()
    {
        lock (_lock)
        {
            var conflicts = new List<PatternConflictInfo>();
            
            foreach (var kvp in _selections.AvailablePatterns)
            {
                if (kvp.Value.Count > 1)
                {
                    var parts = kvp.Key.Split('|');
                    if (parts.Length == 2)
                    {
                        var activeSourceId = _selections.ActiveSelections.GetValueOrDefault(kvp.Key);
                        var activeSource = kvp.Value.FirstOrDefault(s => s.SourceId == activeSourceId);
                        
                        conflicts.Add(new PatternConflictInfo
                        {
                            ShipType = parts[0],
                            EventName = parts[1],
                            AvailablePatterns = kvp.Value.ToList(),
                            ActivePattern = activeSource,
                            ConflictCount = kvp.Value.Count
                        });
                    }
                }
            }
            
            return new PatternConflictSummary
            {
                Conflicts = conflicts,
                TotalConflicts = conflicts.Count,
                TotalAffectedEvents = conflicts.Sum(c => c.ConflictCount - 1) // -1 because one is active
            };
        }
    }

    public void CleanupMissingSources(HashSet<string> validSourceIds)
    {
        lock (_lock)
        {
            var keysToRemove = new List<string>();
            var changes = 0;
            
            foreach (var kvp in _selections.AvailablePatterns)
            {
                var validSources = kvp.Value.Where(s => validSourceIds.Contains(s.SourceId)).ToList();
                
                if (validSources.Count != kvp.Value.Count)
                {
                    if (validSources.Count == 0)
                    {
                        // No valid sources left, remove everything
                        keysToRemove.Add(kvp.Key);
                        _selections.ActiveSelections.Remove(kvp.Key);
                    }
                    else
                    {
                        // Some sources are still valid
                        _selections.AvailablePatterns[kvp.Key] = validSources;
                        
                        // Check if active selection is still valid
                        var activeSourceId = _selections.ActiveSelections.GetValueOrDefault(kvp.Key);
                        if (activeSourceId != null && !validSources.Any(s => s.SourceId == activeSourceId))
                        {
                            // Switch to first valid source
                            _selections.ActiveSelections[kvp.Key] = validSources.First().SourceId;
                            _logger.LogInformation("Switched active pattern for {Key} due to cleanup", kvp.Key);
                        }
                    }
                    changes++;
                }
            }
            
            foreach (var key in keysToRemove)
            {
                _selections.AvailablePatterns.Remove(key);
            }
            
            if (changes > 0)
            {
                _selections.LastUpdated = DateTime.UtcNow;
                _logger.LogInformation("Cleaned up {Changes} pattern selections due to missing sources", changes);
            }
        }
    }

    public PatternSelectionStats GetStats()
    {
        lock (_lock)
        {
            var stats = new PatternSelectionStats
            {
                TotalShipEventCombinations = _selections.AvailablePatterns.Count,
                TotalAvailablePatterns = _selections.AvailablePatterns.Values.Sum(v => v.Count),
                ConflictingCombinations = _selections.AvailablePatterns.Count(kvp => kvp.Value.Count > 1),
                LastUpdated = _selections.LastUpdated
            };

            // Count by source type
            foreach (var sources in _selections.AvailablePatterns.Values)
            {
                foreach (var source in sources)
                {
                    switch (source.SourceType)
                    {
                        case PatternSourceType.FileSystem:
                            stats.FileSystemPatterns++;
                            break;
                        case PatternSourceType.UserCustom:
                            stats.UserCustomPatterns++;
                            break;
                        case PatternSourceType.Default:
                            stats.DefaultPatterns++;
                            break;
                    }
                }
            }

            return stats;
        }
    }

    private static string GetSelectionKey(string shipType, string eventName)
    {
        return $"{shipType.ToLowerInvariant()}|{eventName.ToLowerInvariant()}";
    }
}

// Data Models
public class PatternSelections
{
    public Dictionary<string, List<PatternSourceInfo>> AvailablePatterns { get; set; } = new();
    public Dictionary<string, string> ActiveSelections { get; set; } = new();
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    public int GetTotalSelections()
    {
        return AvailablePatterns.Values.Sum(v => v.Count);
    }
}

public class PatternSourceInfo
{
    public string SourceId { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public PatternSourceType SourceType { get; set; }
    public string PackName { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime LastModified { get; set; }
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new();
    
    // Pattern details for quick reference
    public string PatternType { get; set; } = string.Empty;
    public int Frequency { get; set; }
    public int Intensity { get; set; }
    public int Duration { get; set; }
}

public enum PatternSourceType
{
    Default,     // Built-in default patterns
    FileSystem,  // Loaded from pattern files
    UserCustom  // User's custom overrides
}

public class PatternSelectionChangedEventArgs
{
    public string ShipType { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public string NewSourceId { get; set; } = string.Empty;
    public string? PreviousSourceId { get; set; }
    public PatternSourceInfo SourceInfo { get; set; } = new();
}

public class PatternConflictInfo
{
    public string ShipType { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public List<PatternSourceInfo> AvailablePatterns { get; set; } = new();
    public PatternSourceInfo? ActivePattern { get; set; }
    public int ConflictCount { get; set; }
}

public class PatternConflictSummary
{
    public List<PatternConflictInfo> Conflicts { get; set; } = new();
    public int TotalConflicts { get; set; }
    public int TotalAffectedEvents { get; set; }
}

public class PatternSelectionStats
{
    public int TotalShipEventCombinations { get; set; }
    public int TotalAvailablePatterns { get; set; }
    public int ConflictingCombinations { get; set; }
    public int FileSystemPatterns { get; set; }
    public int UserCustomPatterns { get; set; }
    public int DefaultPatterns { get; set; }
    public DateTime LastUpdated { get; set; }
}