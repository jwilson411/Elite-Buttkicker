using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using EDButtkicker.Hosting;
using EDButtkicker.Services;
using EDButtkicker.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

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
            return StatusCode(500, new { error = "Failed to get pattern conflicts" });
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
            return StatusCode(500, new { error = "Failed to get available patterns" });
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
            return StatusCode(500, new { error = "Failed to select pattern" });
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
            return StatusCode(500, new { error = "Failed to auto-resolve conflicts" });
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
            return StatusCode(500, new { error = "Failed to get stats" });
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
            return StatusCode(500, new { error = "Failed to refresh sources" });
        }
    }

    // HttpContext wrapper methods for the manual routing in WebUiConfiguration.
    //
    // The resolution strategy travels as its name ("LatestModified"), which is what the
    // conflicts page sends and what its result message shows, so these endpoints - and only
    // these - read and write enums as strings.
    private static readonly JsonSerializerOptions ResponseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions RequestJson = new()
    {
        MaxDepth = RequestLimits.MaxJsonDepth,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: true) }
    };

    public Task GetConflictsHttpContext(HttpContext context) => WriteResultAsync(context, GetConflicts().Result);

    public Task GetStatsHttpContext(HttpContext context) => WriteResultAsync(context, GetStats().Result);

    public Task GetAvailablePatternsHttpContext(HttpContext context, string shipType, string eventName) =>
        WriteResultAsync(context, GetAvailablePatterns(shipType, eventName).Result);

    public async Task SelectPatternHttpContext(HttpContext context)
    {
        try
        {
            var request = await ReadBodyAsync<SelectPatternRequest>(context);
            if (request == null)
            {
                return;
            }

            await WriteResultAsync(context, await SelectPattern(request));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error selecting a pattern");
            await ApiError.WriteAsync(context, 500, "Internal server error");
        }
    }

    public async Task AutoResolveConflictsHttpContext(HttpContext context)
    {
        try
        {
            var request = await ReadBodyAsync<AutoResolveRequest>(context);
            if (request == null)
            {
                return;
            }

            await WriteResultAsync(context, (await AutoResolveConflicts(request)).Result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error auto-resolving pattern conflicts");
            await ApiError.WriteAsync(context, 500, "Internal server error");
        }
    }

    public async Task RefreshSourcesHttpContext(HttpContext context)
    {
        try
        {
            await WriteResultAsync(context, (await RefreshSources()).Result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing pattern sources");
            await ApiError.WriteAsync(context, 500, "Internal server error");
        }
    }

    /// <summary>
    /// Reads and deserializes a request body, answering the caller itself when it is missing or
    /// malformed. A null return means the response is already written.
    /// </summary>
    private static async Task<T?> ReadBodyAsync<T>(HttpContext context) where T : class
    {
        var body = await BoundedRequestReader.ReadOrRespondAsync(context, "Invalid request body");
        if (body == null)
        {
            return null;
        }

        if (!BoundedRequestReader.TryDeserialize<T>(body, out var value, RequestJson) || value == null)
        {
            await BoundedRequestReader.WriteErrorAsync(context, 400, "Invalid request body");
            return null;
        }

        return value;
    }

    private static async Task WriteResultAsync(HttpContext context, IActionResult? result)
    {
        context.Response.ContentType = "application/json";

        if (result is ObjectResult objectResult)
        {
            context.Response.StatusCode = objectResult.StatusCode ?? 200;
            await context.Response.WriteAsync(JsonSerializer.Serialize(objectResult.Value, ResponseJson));
            return;
        }

        if (result is StatusCodeResult statusCodeResult)
        {
            context.Response.StatusCode = statusCodeResult.StatusCode;
            return;
        }

        context.Response.StatusCode = 500;
        await context.Response.WriteAsync(JsonSerializer.Serialize(ApiError.Payload("Internal server error")));
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