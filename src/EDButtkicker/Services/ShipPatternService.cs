using Microsoft.Extensions.Logging;
using System.Text.Json;
using EDButtkicker.Models;

namespace EDButtkicker.Services;

public class ShipPatternService
{
    private readonly ILogger<ShipPatternService> _logger;
    private readonly EventMappingService _eventMappingService;
    private readonly PatternFileService _patternFileService;
    private readonly PatternSelectionService _patternSelectionService;
    private readonly string _shipPatternsPath;
    private readonly JsonSerializerOptions _jsonOptions;
    
    private ShipPatternLibrary _patternLibrary = new();
    private readonly object _lock = new object();

    public ShipPatternService(ILogger<ShipPatternService> logger, EventMappingService eventMappingService, PatternFileService patternFileService, PatternSelectionService patternSelectionService)
    {
        _logger = logger;
        _eventMappingService = eventMappingService;
        _patternFileService = patternFileService;
        _patternSelectionService = patternSelectionService;
        
        // Store ship patterns in user's AppData
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var settingsDir = Path.Combine(appDataPath, "EDButtkicker");
        Directory.CreateDirectory(settingsDir);
        
        _shipPatternsPath = Path.Combine(settingsDir, "ship-patterns.json");
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            AllowTrailingCommas = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        
        _logger.LogDebug("ShipPatternService initialized with path: {PatternsPath}", _shipPatternsPath);
    }

    public async Task LoadShipPatternsAsync()
    {
        try
        {
            if (!File.Exists(_shipPatternsPath))
            {
                _logger.LogInformation("No ship patterns file found, starting with empty library");
                return;
            }

            var json = await File.ReadAllTextAsync(_shipPatternsPath);
            var library = JsonSerializer.Deserialize<ShipPatternLibrary>(json, _jsonOptions);
            
            if (library != null)
            {
                lock (_lock)
                {
                    _patternLibrary = library;
                }
                _logger.LogInformation("Loaded ship patterns for {ShipCount} ships with {PatternCount} total patterns", 
                    library.Ships.Count, library.GetTotalCustomPatterns());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading ship patterns, starting with empty library");
            lock (_lock)
            {
                _patternLibrary = new ShipPatternLibrary();
            }
        }
    }

    public async Task SaveShipPatternsAsync()
    {
        try
        {
            ShipPatternLibrary libraryToSave;
            lock (_lock)
            {
                libraryToSave = _patternLibrary;
            }

            var json = JsonSerializer.Serialize(libraryToSave, _jsonOptions);
            await File.WriteAllTextAsync(_shipPatternsPath, json);
            
            _logger.LogInformation("Saved ship patterns for {ShipCount} ships with {PatternCount} total patterns", 
                libraryToSave.Ships.Count, libraryToSave.GetTotalCustomPatterns());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving ship patterns");
            throw;
        }
    }

    public void SetCurrentShip(CurrentShip ship)
    {
        lock (_lock)
        {
            var previousShipKey = _patternLibrary.CurrentShipKey;
            _patternLibrary.SetCurrentShip(ship);
            
            if (previousShipKey != _patternLibrary.CurrentShipKey)
            {
                _logger.LogInformation("Active ship changed to {ShipName} ({ShipType}) - Key: {ShipKey}", 
                    ship.ShipName, ship.ShipType, ship.GetShipKey());
                    
                LogCurrentShipPatternSummary();
            }
        }
    }

    public CurrentShip? GetCurrentShip()
    {
        lock (_lock)
        {
            var currentPatterns = _patternLibrary.GetCurrentShipPatterns();
            if (currentPatterns != null)
            {
                return new CurrentShip
                {
                    ShipType = currentPatterns.ShipType,
                    ShipName = currentPatterns.ShipName,
                    LastUpdated = currentPatterns.LastModified
                };
            }
            return null;
        }
    }

    public HapticPattern? GetPatternForEvent(string eventName)
    {
        lock (_lock)
        {
            var currentShip = _patternLibrary.GetCurrentShipPatterns();
            if (currentShip != null)
            {
                // First priority: User's custom pattern overrides (highest priority)
                if (currentShip.EventPatterns.TryGetValue(eventName, out var customPattern))
                {
                    _logger.LogDebug("Using user custom pattern for {EventName} on {ShipKey}", 
                        eventName, _patternLibrary.CurrentShipKey);
                    return customPattern;
                }
                
                // Second priority: Selected pattern from pattern selection service
                var activePatternInfo = _patternSelectionService.GetActivePatternInfo(currentShip.ShipType, eventName);
                if (activePatternInfo != null)
                {
                    // Get the actual pattern from the selected source
                    HapticPattern? selectedPattern = null;
                    
                    switch (activePatternInfo.SourceType)
                    {
                        case PatternSourceType.FileSystem:
                            selectedPattern = _patternFileService.GetPatternForShipEvent(currentShip.ShipType, eventName, activePatternInfo.PackName);
                            break;
                        case PatternSourceType.Default:
                            selectedPattern = _eventMappingService.GetDefaultPatternForEvent(eventName);
                            break;
                        // UserCustom patterns are handled above
                    }
                    
                    if (selectedPattern != null)
                    {
                        _logger.LogDebug("Using selected pattern '{SourceName}' for {EventName} on {ShipType}", 
                            activePatternInfo.SourceName, eventName, currentShip.ShipType);
                        return selectedPattern;
                    }
                }
                
                // Third priority: Any available file-based pattern (for backward compatibility)
                var filePattern = _patternFileService.GetPatternForShipEvent(currentShip.ShipType, eventName);
                if (filePattern != null)
                {
                    _logger.LogDebug("Using fallback file-based pattern for {EventName} on {ShipType}", 
                        eventName, currentShip.ShipType);
                    return filePattern;
                }
            }
            
            // Final fallback: Default pattern
            var defaultPattern = _eventMappingService.GetDefaultPatternForEvent(eventName);
            _logger.LogDebug("Using default pattern for {EventName}", eventName);
            return defaultPattern;
        }
    }

    public async Task SetShipPatternAsync(string shipKey, string eventName, HapticPattern pattern)
    {
        lock (_lock)
        {
            if (!_patternLibrary.Ships.TryGetValue(shipKey, out var shipPatterns))
            {
                _logger.LogWarning("Attempted to set pattern for unknown ship: {ShipKey}", shipKey);
                return;
            }

            shipPatterns.SetPatternForEvent(eventName, pattern);
            _patternLibrary.LastUpdated = DateTime.UtcNow;
            
            _logger.LogInformation("Set custom pattern for {EventName} on ship {ShipKey}", eventName, shipKey);
        }

        await SaveShipPatternsAsync();
    }

    public async Task RemoveShipPatternAsync(string shipKey, string eventName)
    {
        lock (_lock)
        {
            if (_patternLibrary.Ships.TryGetValue(shipKey, out var shipPatterns))
            {
                shipPatterns.RemovePatternForEvent(eventName);
                _patternLibrary.LastUpdated = DateTime.UtcNow;
                
                _logger.LogInformation("Removed custom pattern for {EventName} on ship {ShipKey}", eventName, shipKey);
            }
        }

        await SaveShipPatternsAsync();
    }

    public ShipPatternLibrary GetPatternLibrary()
    {
        lock (_lock)
        {
            // Return a deep copy to prevent external modification
            var json = JsonSerializer.Serialize(_patternLibrary, _jsonOptions);
            return JsonSerializer.Deserialize<ShipPatternLibrary>(json, _jsonOptions) ?? new ShipPatternLibrary();
        }
    }

    public List<ShipSpecificPatterns> GetAllShipPatterns()
    {
        lock (_lock)
        {
            return _patternLibrary.GetAllShipPatterns();
        }
    }

    public ShipSpecificPatterns? GetShipPatterns(string shipKey)
    {
        lock (_lock)
        {
            return _patternLibrary.Ships.TryGetValue(shipKey, out var patterns) ? patterns : null;
        }
    }

    public async Task RemoveShipAsync(string shipKey)
    {
        lock (_lock)
        {
            _patternLibrary.RemoveShip(shipKey);
            _logger.LogInformation("Removed ship patterns for {ShipKey}", shipKey);
        }

        await SaveShipPatternsAsync();
    }

    public PatternRecommendations GetRecommendationsForShip(string shipType)
    {
        // Try to get recommendations from pattern files first
        var fileBasedPatterns = _patternFileService.GetPatternsForShip(shipType);
        if (fileBasedPatterns.Any())
        {
            // Build recommendations based on available file patterns
            var fileRecommendation = new PatternRecommendations
            {
                ShipSize = ShipSize.Medium, // Will be overridden from file metadata
                ShipRole = ShipRole.Multipurpose, // Will be overridden from file metadata
                SuggestedPatterns = new Dictionary<string, string>()
            };
            
            // Get ship metadata from first pattern file
            var firstPattern = fileBasedPatterns.FirstOrDefault();
            if (firstPattern != null)
            {
                fileRecommendation.ShipSize = ParseShipSize(firstPattern.Class);
                fileRecommendation.ShipRole = ParseShipRole(firstPattern.Role);
                
                foreach (var eventName in firstPattern.Events.Keys)
                {
                    fileRecommendation.SuggestedPatterns[eventName] = $"From {firstPattern.PackName}";
                }
            }
            
            return fileRecommendation;
        }
        
        // Fall back to hard-coded classifications
        return ShipClassifications.GetPatternRecommendations(shipType);
    }

    public async Task ApplyRecommendedPatternsAsync(string shipKey, string shipType)
    {
        int appliedCount = 0;
        
        lock (_lock)
        {
            if (!_patternLibrary.Ships.TryGetValue(shipKey, out var shipPatterns))
            {
                _logger.LogWarning("Cannot apply recommendations to unknown ship: {ShipKey}", shipKey);
                return;
            }

            // First, try to apply patterns from pattern files
            var fileBasedPatterns = _patternFileService.GetPatternsForShip(shipType);
            
            if (fileBasedPatterns.Any())
            {
                var preferredPattern = fileBasedPatterns.FirstOrDefault();
                if (preferredPattern != null)
                {
                    foreach (var eventPattern in preferredPattern.Events)
                    {
                        var eventName = eventPattern.Key;
                        if (!shipPatterns.HasCustomPatternForEvent(eventName))
                        {
                            // Use the file-based pattern directly (it will be picked up by GetPatternForEvent)
                            // No need to store it as custom since file-based patterns take precedence
                            appliedCount++;
                        }
                    }
                }
                
                _logger.LogInformation("Applied {Count} file-based patterns to ship {ShipKey} from pack {PackName}", 
                    appliedCount, shipKey, fileBasedPatterns.First().PackName);
            }
            else
            {
                // Fall back to hard-coded recommendations
                var recommendations = ShipClassifications.GetPatternRecommendations(shipType);
                
                foreach (var suggestion in recommendations.SuggestedPatterns)
                {
                    var eventName = suggestion.Key;
                    var defaultPattern = _eventMappingService.GetDefaultPatternForEvent(eventName);
                    
                    if (defaultPattern != null && !shipPatterns.HasCustomPatternForEvent(eventName))
                    {
                        // Create modified pattern based on recommendations
                        var modifiedPattern = CreateModifiedPattern(defaultPattern, recommendations, suggestion.Value);
                        shipPatterns.SetPatternForEvent(eventName, modifiedPattern);
                        appliedCount++;
                    }
                }
                
                _patternLibrary.LastUpdated = DateTime.UtcNow;
                _logger.LogInformation("Applied {Count} hard-coded recommended patterns to ship {ShipKey}", 
                    appliedCount, shipKey);
            }
        }

        if (appliedCount > 0)
        {
            await SaveShipPatternsAsync();
        }
    }

    private HapticPattern CreateModifiedPattern(HapticPattern basePattern, PatternRecommendations recommendations, string description)
    {
        var modifiedPattern = new HapticPattern
        {
            Name = $"{basePattern.Name} ({recommendations.ShipSize})",
            Pattern = basePattern.Pattern,
            Frequency = Math.Max(10, Math.Min(100, basePattern.Frequency + recommendations.RecommendedFrequencyAdjustment)),
            Intensity = Math.Max(10, Math.Min(100, (int)(basePattern.Intensity * recommendations.RecommendedIntensityMultiplier))),
            Duration = Math.Max(100, (int)(basePattern.Duration * recommendations.RecommendedDurationMultiplier)),
            FadeIn = (int)(basePattern.FadeIn * recommendations.RecommendedDurationMultiplier),
            FadeOut = (int)(basePattern.FadeOut * recommendations.RecommendedDurationMultiplier),
            IntensityFromDamage = basePattern.IntensityFromDamage,
            MinIntensity = Math.Max(5, (int)(basePattern.MinIntensity * recommendations.RecommendedIntensityMultiplier)),
            MaxIntensity = Math.Max(20, Math.Min(100, (int)(basePattern.MaxIntensity * recommendations.RecommendedIntensityMultiplier))),
            IntensityCurve = basePattern.IntensityCurve,
            CustomCurvePoints = basePattern.CustomCurvePoints?.ToList()
        };

        return modifiedPattern;
    }

    public ShipPatternStats GetStats()
    {
        lock (_lock)
        {
            var stats = new ShipPatternStats
            {
                TotalShips = _patternLibrary.Ships.Count,
                TotalCustomPatterns = _patternLibrary.GetTotalCustomPatterns(),
                CurrentShipKey = _patternLibrary.CurrentShipKey,
                LastUpdated = _patternLibrary.LastUpdated
            };

            // Count patterns by ship size
            foreach (var ship in _patternLibrary.Ships.Values)
            {
                var shipClass = ShipClassifications.GetShipClass(ship.ShipType);
                switch (shipClass.Size)
                {
                    case ShipSize.Small:
                        stats.SmallShipPatterns += ship.EventPatterns.Count;
                        break;
                    case ShipSize.Medium:
                        stats.MediumShipPatterns += ship.EventPatterns.Count;
                        break;
                    case ShipSize.Large:
                        stats.LargeShipPatterns += ship.EventPatterns.Count;
                        break;
                }
            }

            return stats;
        }
    }

    private void LogCurrentShipPatternSummary()
    {
        try
        {
            var currentPatterns = _patternLibrary.GetCurrentShipPatterns();
            if (currentPatterns != null)
            {
                var customEventCount = currentPatterns.EventPatterns.Count;
                var recommendations = GetRecommendationsForShip(currentPatterns.ShipType);
                
                _logger.LogInformation("Ship pattern summary - {ShipName}: {CustomPatterns} custom patterns, {ShipClass} class", 
                    currentPatterns.ShipName, customEventCount, recommendations.ShipSize);
                    
                if (customEventCount > 0)
                {
                    _logger.LogDebug("Custom events: {Events}", string.Join(", ", currentPatterns.EventPatterns.Keys));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error logging ship pattern summary");
        }
    }

    private static ShipSize ParseShipSize(string shipClass)
    {
        return shipClass.ToLowerInvariant() switch
        {
            "small" => ShipSize.Small,
            "medium" => ShipSize.Medium,
            "large" => ShipSize.Large,
            _ => ShipSize.Medium
        };
    }

    private static ShipRole ParseShipRole(string shipRole)
    {
        return shipRole.ToLowerInvariant() switch
        {
            "combat" => ShipRole.Combat,
            "exploration" => ShipRole.Exploration,
            "trading" => ShipRole.Transport,
            "transport" => ShipRole.Transport,
            "mining" => ShipRole.Transport,
            "multipurpose" => ShipRole.Multipurpose,
            _ => ShipRole.Multipurpose
        };
    }
}

public class ShipPatternStats
{
    public int TotalShips { get; set; }
    public int TotalCustomPatterns { get; set; }
    public int SmallShipPatterns { get; set; }
    public int MediumShipPatterns { get; set; }
    public int LargeShipPatterns { get; set; }
    public string? CurrentShipKey { get; set; }
    public DateTime LastUpdated { get; set; }
}