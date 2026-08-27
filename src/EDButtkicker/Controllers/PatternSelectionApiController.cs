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
    private readonly PatternSourceCatalogReconciler _catalogReconciler;

    public PatternSelectionController(
        ILogger<PatternSelectionController> logger,
        PatternSelectionService patternSelectionService,
        PatternSourceCatalogReconciler catalogReconciler)
    {
        _logger = logger;
        _patternSelectionService = patternSelectionService;
        _catalogReconciler = catalogReconciler;
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
            // Registers every ship type the pattern catalog knows about, cleans up dead
            // selections and saves - the same reconcile that runs at startup and on file changes.
            var totalSources = await _catalogReconciler.ReconcileAsync();

            var stats = _patternSelectionService.GetStats();
            var conflicts = _patternSelectionService.GetConflicts();

            return Ok(new RefreshSourcesResponse
            {
                Message = "Pattern sources refreshed successfully",
                TotalSources = totalSources,
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