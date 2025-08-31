using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using EDButtkicker.Services;
using EDButtkicker.Models;

namespace EDButtkicker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PatternSelectionController : ControllerBase
{
    private readonly ILogger<PatternSelectionController> _logger;
    private readonly PatternSelectionService _patternSelectionService;
    private readonly PatternFileService _patternFileService;

    public PatternSelectionController(
        ILogger<PatternSelectionController> logger,
        PatternSelectionService patternSelectionService,
        PatternFileService patternFileService)
    {
        _logger = logger;
        _patternSelectionService = patternSelectionService;
        _patternFileService = patternFileService;
    }

    [HttpGet("conflicts")]
    public ActionResult<PatternConflictSummary> GetConflicts()
    {
        try
        {
            var conflicts = _patternSelectionService.GetConflicts();
            return Ok(conflicts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting pattern conflicts");
            return StatusCode(500, new { error = "Failed to get pattern conflicts", details = ex.Message });
        }
    }

    [HttpGet("available/{shipType}/{eventName}")]
    public ActionResult<PatternOptionsResponse> GetAvailablePatterns(string shipType, string eventName)
    {
        try
        {
            var availablePatterns = _patternSelectionService.GetAvailablePatterns(shipType, eventName);
            var activePatternInfo = _patternSelectionService.GetActivePatternInfo(shipType, eventName);
            
            return Ok(new PatternOptionsResponse
            {
                ShipType = shipType,
                EventName = eventName,
                AvailablePatterns = availablePatterns,
                ActivePattern = activePatternInfo,
                HasConflicts = availablePatterns.Count > 1
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting available patterns for {ShipType}.{EventName}", shipType, eventName);
            return StatusCode(500, new { error = "Failed to get available patterns", details = ex.Message });
        }
    }

    [HttpPost("select")]
    public async Task<ActionResult> SelectPattern([FromBody] SelectPatternRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.ShipType) || string.IsNullOrEmpty(request.EventName) || string.IsNullOrEmpty(request.SourceId))
            {
                return BadRequest(new { error = "ShipType, EventName, and SourceId are required" });
            }

            _patternSelectionService.SetActivePattern(request.ShipType, request.EventName, request.SourceId);
            await _patternSelectionService.SaveSelectionsAsync();

            var selectedInfo = _patternSelectionService.GetActivePatternInfo(request.ShipType, request.EventName);

            return Ok(new SelectPatternResponse
            {
                Message = $"Selected pattern '{selectedInfo?.SourceName}' for {request.ShipType} {request.EventName}",
                SelectedPattern = selectedInfo,
                Success = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error selecting pattern for {ShipType}.{EventName}: {SourceId}", 
                request.ShipType, request.EventName, request.SourceId);
            return StatusCode(500, new { error = "Failed to select pattern", details = ex.Message });
        }
    }

    [HttpPost("auto-resolve")]
    public async Task<ActionResult<AutoResolveResponse>> AutoResolveConflicts([FromBody] AutoResolveRequest request)
    {
        try
        {
            var conflicts = _patternSelectionService.GetConflicts();
            var resolvedCount = 0;
            var resolvedConflicts = new List<ResolvedConflictInfo>();

            foreach (var conflict in conflicts.Conflicts)
            {
                PatternSourceInfo? selectedPattern = null;

                switch (request.ResolutionStrategy)
                {
                    case ConflictResolutionStrategy.LatestVersion:
                        selectedPattern = conflict.AvailablePatterns
                            .OrderByDescending(p => Version.TryParse(p.Version, out var v) ? v : new Version(0, 0))
                            .ThenByDescending(p => p.LastModified)
                            .First();
                        break;

                    case ConflictResolutionStrategy.LatestModified:
                        selectedPattern = conflict.AvailablePatterns
                            .OrderByDescending(p => p.LastModified)
                            .First();
                        break;

                    case ConflictResolutionStrategy.PreferFileSystem:
                        selectedPattern = conflict.AvailablePatterns
                            .Where(p => p.SourceType == PatternSourceType.FileSystem)
                            .OrderByDescending(p => p.LastModified)
                            .FirstOrDefault() ?? conflict.AvailablePatterns.First();
                        break;

                    case ConflictResolutionStrategy.PreferUserCustom:
                        selectedPattern = conflict.AvailablePatterns
                            .Where(p => p.SourceType == PatternSourceType.UserCustom)
                            .OrderByDescending(p => p.LastModified)
                            .FirstOrDefault() ?? conflict.AvailablePatterns.First();
                        break;

                    case ConflictResolutionStrategy.KeepCurrent:
                        // Skip if we want to keep current selection
                        continue;
                }

                if (selectedPattern != null && selectedPattern.SourceId != conflict.ActivePattern?.SourceId)
                {
                    _patternSelectionService.SetActivePattern(conflict.ShipType, conflict.EventName, selectedPattern.SourceId);
                    resolvedCount++;

                    resolvedConflicts.Add(new ResolvedConflictInfo
                    {
                        ShipType = conflict.ShipType,
                        EventName = conflict.EventName,
                        PreviousPattern = conflict.ActivePattern,
                        NewPattern = selectedPattern,
                        ResolutionReason = request.ResolutionStrategy.ToString()
                    });
                }
            }

            if (resolvedCount > 0)
            {
                await _patternSelectionService.SaveSelectionsAsync();
            }

            return Ok(new AutoResolveResponse
            {
                ResolvedCount = resolvedCount,
                TotalConflicts = conflicts.TotalConflicts,
                ResolutionStrategy = request.ResolutionStrategy,
                ResolvedConflicts = resolvedConflicts
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error auto-resolving pattern conflicts");
            return StatusCode(500, new { error = "Failed to auto-resolve conflicts", details = ex.Message });
        }
    }

    [HttpGet("stats")]
    public ActionResult<PatternSelectionStats> GetStats()
    {
        try
        {
            var stats = _patternSelectionService.GetStats();
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting pattern selection stats");
            return StatusCode(500, new { error = "Failed to get stats", details = ex.Message });
        }
    }

    [HttpPost("refresh-sources")]
    public async Task<ActionResult<RefreshSourcesResponse>> RefreshSources()
    {
        try
        {
            // Get all current pattern sources from various places
            var allSourceIds = new HashSet<string>();
            
            // Add sources from pattern files by getting all ship types
            var allShipTypes = new HashSet<string>();
            
            // Get all available ship types from the pattern file service
            var allPacks = _patternFileService.GetAllPatternPacks();
            foreach (var pack in allPacks)
            {
                // We need to iterate through all pattern data to find ship types
                // This is a limitation of the current pattern file structure
            }
            
            // For now, we'll use a more direct approach by getting patterns for known ship types
            // In a real implementation, we'd want the pattern service to provide a list of all ship types
            var knownShipTypes = new[]
            {
                "sidewinder", "eagle", "hauler", "adder", "viper", "cobra_mkiii", "type6", 
                "asp", "vulture", "python", "type7", "anaconda", "federation_corvette", 
                "cutter", "type9", "krait_mkii", "chieftain", "challenger"
                // Add more as needed
            };
            
            foreach (var shipType in knownShipTypes)
            {
                var shipPatterns = _patternFileService.GetPatternsForShip(shipType);
                foreach (var shipPattern in shipPatterns)
                {
                    foreach (var eventName in shipPattern.Events.Keys)
                    {
                        var sourceId = GenerateSourceId(PatternSourceType.FileSystem, shipPattern.PackName, shipType, eventName);
                        allSourceIds.Add(sourceId);
                        
                        // Register this pattern source
                        var sourceInfo = new PatternSourceInfo
                        {
                            SourceId = sourceId,
                            SourceName = $"{shipPattern.PackName} - {shipPattern.DisplayName}",
                            SourceType = PatternSourceType.FileSystem,
                            PackName = shipPattern.PackName,
                            Author = shipPattern.Author,
                            Version = shipPattern.Version,
                            LastModified = DateTime.UtcNow, // Would be file modified time in real implementation
                            Description = "", // ShipPatternDefinition doesn't have description
                            Tags = shipPattern.Tags,
                            PatternType = shipPattern.Events[eventName].Pattern.ToString(),
                            Frequency = shipPattern.Events[eventName].Frequency,
                            Intensity = shipPattern.Events[eventName].Intensity,
                            Duration = shipPattern.Events[eventName].Duration
                        };
                        
                        _patternSelectionService.RegisterPatternSource(shipType, eventName, sourceInfo);
                    }
                }
            }
            
            // Clean up any sources that no longer exist
            _patternSelectionService.CleanupMissingSources(allSourceIds);
            
            // Save the updated selections
            await _patternSelectionService.SaveSelectionsAsync();
            
            var stats = _patternSelectionService.GetStats();
            var conflicts = _patternSelectionService.GetConflicts();
            
            return Ok(new RefreshSourcesResponse
            {
                Message = "Pattern sources refreshed successfully",
                TotalSources = allSourceIds.Count,
                TotalConflicts = conflicts.TotalConflicts,
                Stats = stats
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing pattern sources");
            return StatusCode(500, new { error = "Failed to refresh sources", details = ex.Message });
        }
    }

    private static string GenerateSourceId(PatternSourceType sourceType, string packName, string shipType, string eventName)
    {
        return $"{sourceType}:{packName}:{shipType}:{eventName}".ToLowerInvariant();
    }
}

// Request/Response DTOs
public class PatternOptionsResponse
{
    public string ShipType { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public List<PatternSourceInfo> AvailablePatterns { get; set; } = new();
    public PatternSourceInfo? ActivePattern { get; set; }
    public bool HasConflicts { get; set; }
}

public class SelectPatternRequest
{
    public string ShipType { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
}

public class SelectPatternResponse
{
    public string Message { get; set; } = string.Empty;
    public PatternSourceInfo? SelectedPattern { get; set; }
    public bool Success { get; set; }
}

public class AutoResolveRequest
{
    public ConflictResolutionStrategy ResolutionStrategy { get; set; }
}

public enum ConflictResolutionStrategy
{
    KeepCurrent,
    LatestVersion,
    LatestModified,
    PreferFileSystem,
    PreferUserCustom
}

public class AutoResolveResponse
{
    public int ResolvedCount { get; set; }
    public int TotalConflicts { get; set; }
    public ConflictResolutionStrategy ResolutionStrategy { get; set; }
    public List<ResolvedConflictInfo> ResolvedConflicts { get; set; } = new();
}

public class ResolvedConflictInfo
{
    public string ShipType { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public PatternSourceInfo? PreviousPattern { get; set; }
    public PatternSourceInfo NewPattern { get; set; } = new();
    public string ResolutionReason { get; set; } = string.Empty;
}

public class RefreshSourcesResponse
{
    public string Message { get; set; } = string.Empty;
    public int TotalSources { get; set; }
    public int TotalConflicts { get; set; }
    public PatternSelectionStats Stats { get; set; } = new();
}