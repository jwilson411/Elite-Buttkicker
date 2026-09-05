using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using EDButtkicker.Hosting;
using EDButtkicker.Services;
using EDButtkicker.Models;

namespace EDButtkicker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PatternFilesController : ControllerBase
{
    private static readonly string[] DeletableSubdirectories = { "imports", "exports", "Custom" };
    private static readonly string?[] SystemSubdirectories = { "Community", "Small_Ships", "Large_Ships", null };

    private readonly ILogger<PatternFilesController> _logger;
    private readonly PatternFileService _patternFileService;

    private static string PatternsRoot => Path.Combine(Directory.GetCurrentDirectory(), "patterns");

    public PatternFilesController(
        ILogger<PatternFilesController> logger,
        PatternFileService patternFileService)
    {
        _logger = logger;
        _patternFileService = patternFileService;
    }

    [HttpGet("packs")]
    public ActionResult<PatternPacksResponse> GetAllPatternPacks()
    {
        try
        {
            var packs = _patternFileService.GetAllPatternPacks();
            var totalPatterns = packs.Sum(p => p.PatternCount);
            var totalShips = packs.Sum(p => p.ShipCount);

            return Ok(new PatternPacksResponse
            {
                Packs = packs,
                Stats = new PatternFileStats
                {
                    TotalPacks = packs.Count,
                    TotalShips = totalShips,
                    TotalPatterns = totalPatterns,
                    Authors = packs.Select(p => p.Author).Distinct().ToList(),
                    Tags = packs.SelectMany(p => p.Tags).Distinct().OrderBy(t => t).ToList()
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting pattern packs");
            return StatusCode(500, new { error = "Failed to get pattern packs" });
        }
    }

    [HttpGet("ships")]
    public ActionResult<ShipPatternsResponse> GetShipPatterns()
    {
        try
        {
            var shipTypes = _patternFileService.GetAllShipTypes();
            var shipPatterns = shipTypes.ToDictionary(
                shipType => shipType,
                shipType => _patternFileService.GetPatternsForShip(shipType)
            );

            return Ok(new ShipPatternsResponse
            {
                Ships = shipPatterns.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Select(p => new ShipPatternSummary
                    {
                        ShipType = p.ShipType,
                        DisplayName = p.DisplayName,
                        Class = p.Class,
                        Role = p.Role,
                        PackName = p.PackName,
                        Author = p.Author,
                        Version = p.Version,
                        SourceFile = p.SourceFile,
                        EventCount = p.Events.Count,
                        Events = p.Events.Keys.ToList(),
                        Tags = p.Tags
                    }).ToList()
                ),
                TotalShipTypes = shipTypes.Count,
                TotalDefinitions = shipPatterns.Values.Sum(list => list.Count)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting ship patterns");
            return StatusCode(500, new { error = "Failed to get ship patterns" });
        }
    }

    [HttpGet("ships/{shipType}")]
    public ActionResult<ShipTypePatterns> GetShipTypePatterns(string shipType)
    {
        try
        {
            var patterns = _patternFileService.GetPatternsForShip(shipType);
            
            if (!patterns.Any())
            {
                return NotFound(new { error = $"No patterns found for ship type: {shipType}" });
            }

            var allEvents = patterns.SelectMany(p => p.Events.Keys).Distinct().OrderBy(e => e).ToList();
            var eventPatterns = new Dictionary<string, List<EventPatternSource>>();

            foreach (var eventName in allEvents)
            {
                eventPatterns[eventName] = patterns
                    .Where(p => p.Events.ContainsKey(eventName))
                    .Select(p => new EventPatternSource
                    {
                        Pattern = p.Events[eventName],
                        PackName = p.PackName,
                        Author = p.Author,
                        Version = p.Version,
                        SourceFile = p.SourceFile,
                        Tags = p.Tags
                    })
                    .ToList();
            }

            return Ok(new ShipTypePatterns
            {
                ShipType = shipType,
                DisplayName = patterns.FirstOrDefault()?.DisplayName ?? shipType,
                Class = patterns.FirstOrDefault()?.Class ?? "unknown",
                Role = patterns.FirstOrDefault()?.Role ?? "unknown",
                AvailablePacks = patterns.Select(p => p.PackName).Distinct().ToList(),
                EventPatterns = eventPatterns,
                TotalPatterns = patterns.Sum(p => p.Events.Count)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting patterns for ship type: {ShipType}", shipType);
            return StatusCode(500, new { error = "Failed to get ship type patterns" });
        }
    }

    [HttpGet("ships/{shipType}/events/{eventName}")]
    public ActionResult<EventPatternOptions> GetEventPatternOptions(string shipType, string eventName, [FromQuery] string? preferredPack = null)
    {
        try
        {
            var patterns = _patternFileService.GetPatternsForShip(shipType);
            var eventSources = patterns
                .Where(p => p.Events.ContainsKey(eventName))
                .Select(p => new EventPatternSource
                {
                    Pattern = p.Events[eventName],
                    PackName = p.PackName,
                    Author = p.Author,
                    Version = p.Version,
                    SourceFile = p.SourceFile,
                    Tags = p.Tags
                })
                .ToList();

            if (!eventSources.Any())
            {
                return NotFound(new { error = $"No patterns found for {shipType} event {eventName}" });
            }

            // Get the recommended pattern (preferred pack or first available)
            var recommendedPattern = _patternFileService.GetPatternForShipEvent(shipType, eventName, preferredPack);

            return Ok(new EventPatternOptions
            {
                ShipType = shipType,
                EventName = eventName,
                RecommendedPattern = recommendedPattern,
                AvailablePatterns = eventSources,
                PreferredPack = preferredPack
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting event pattern options for {ShipType}.{EventName}", shipType, eventName);
            return StatusCode(500, new { error = "Failed to get event pattern options" });
        }
    }

    [HttpPost("reload")]
    public async Task<ActionResult<PatternReloadResponse>> ReloadPatternFiles()
    {
        try
        {
            var beforeCount = _patternFileService.GetAllPatternPacks().Count;
            await _patternFileService.LoadAllPatternsAsync();
            var afterCount = _patternFileService.GetAllPatternPacks().Count;
            var allPacks = _patternFileService.GetAllPatternPacks();
            
            return Ok(new PatternReloadResponse
            {
                Message = "Pattern files reloaded successfully",
                TotalPacks = afterCount,
                NewPacks = Math.Max(0, afterCount - beforeCount),
                PackNames = allPacks.Select(p => p.Name).ToList(),
                ReloadTimestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reloading pattern files");
            return StatusCode(500, new { error = "Failed to reload pattern files" });
        }
    }

    [HttpPost("export")]
    public async Task<ActionResult<PatternExportResponse>> ExportPatternPack([FromBody] ExportPatternRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.PackName) || !request.ShipTypes.Any())
            {
                return BadRequest(new { error = "PackName and ShipTypes are required" });
            }

            var exportPath = await _patternFileService.ExportPatternPackAsync(request.PackName, request.ShipTypes);
            var fileInfo = new FileInfo(exportPath);

            return Ok(new PatternExportResponse
            {
                Message = $"Exported pattern pack '{request.PackName}' successfully",
                ExportPath = exportPath,
                FileName = Path.GetFileName(exportPath),
                FileSize = fileInfo.Length,
                ShipCount = request.ShipTypes.Count,
                Created = fileInfo.CreationTime
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting pattern pack: {PackName}", request.PackName);
            return StatusCode(500, new { error = "Failed to export pattern pack" });
        }
    }

    [HttpPost("import")]
    public async Task<ActionResult> ImportPatternFile([FromForm] IFormFile file)
    {
        try
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(new { error = "No file provided" });
            }

            if (!file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { error = "Only JSON files are supported" });
            }

            var safeFileName = PatternPathGuard.SanitizeFileName(file.FileName, requireJsonExtension: true);
            if (safeFileName == null)
            {
                return BadRequest(new { error = "Invalid file name" });
            }

            // The declared length is refused before a byte is written; the bounded copy below is what
            // actually enforces the cap, because the declaration is the caller's word for it.
            if (file.Length > RequestLimits.MaxRequestBodyBytes)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge,
                    new { error = RequestLimits.UploadTooLargeError });
            }

            // Save to temporary location first
            var tempPath = Path.GetTempFileName();
            bool withinLimit;
            using (var stream = new FileStream(tempPath, FileMode.Create))
            {
                withinLimit = await BoundedRequestReader.CopyBoundedAsync(
                    file.OpenReadStream(),
                    stream,
                    RequestLimits.MaxRequestBodyBytes,
                    HttpContext?.RequestAborted ?? CancellationToken.None);
            }

            if (!withinLimit)
            {
                System.IO.File.Delete(tempPath);
                return StatusCode(StatusCodes.Status413PayloadTooLarge,
                    new { error = RequestLimits.UploadTooLargeError });
            }

            // Import the file
            var success = await _patternFileService.ImportPatternFileAsync(tempPath, safeFileName);

            // Clean up temp file
            System.IO.File.Delete(tempPath);

            if (success)
            {
                return Ok(new {
                    message = $"Pattern file '{safeFileName}' imported successfully",
                    fileName = safeFileName,
                    fileSize = file.Length
                });
            }
            else
            {
                return BadRequest(new { error = "Invalid pattern file format" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing pattern file");
            return StatusCode(500, new { error = "Failed to import pattern file" });
        }
    }

    [HttpGet("download/{fileName}")]
    public ActionResult DownloadPatternFile(string fileName)
    {
        try
        {
            var safeFileName = PatternPathGuard.SanitizeFileName(fileName);
            var filePath = PatternPathGuard.ResolveUnderRoot(PatternsRoot, safeFileName, "exports");
            if (safeFileName == null || filePath == null)
            {
                return BadRequest(new { error = "Invalid file name" });
            }

            if (!System.IO.File.Exists(filePath))
            {
                return NotFound(new { error = $"File not found: {safeFileName}" });
            }

            var fileBytes = System.IO.File.ReadAllBytes(filePath);
            return base.File(fileBytes, "application/json", safeFileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading pattern file: {FileName}", fileName);
            return StatusCode(500, new { error = "Failed to download pattern file" });
        }
    }

    [HttpDelete("packs/{fileName}")]
    public ActionResult DeletePatternFile(string fileName)
    {
        try
        {
            var safeFileName = PatternPathGuard.SanitizeFileName(fileName);
            if (safeFileName == null)
            {
                return BadRequest(new { error = "Invalid file name" });
            }

            // Only files in these directories may be deleted; the caller never picks the directory.
            foreach (var subdirectory in DeletableSubdirectories)
            {
                var candidate = PatternPathGuard.ResolveUnderRoot(PatternsRoot, safeFileName, subdirectory);
                if (candidate != null && System.IO.File.Exists(candidate))
                {
                    System.IO.File.Delete(candidate);
                    return Ok(new { message = $"Pattern file '{safeFileName}' deleted successfully" });
                }
            }

            foreach (var subdirectory in SystemSubdirectories)
            {
                var candidate = PatternPathGuard.ResolveUnderRoot(PatternsRoot, safeFileName, subdirectory);
                if (candidate != null && System.IO.File.Exists(candidate))
                {
                    return BadRequest(new { error = "Cannot delete system pattern files" });
                }
            }

            return NotFound(new { error = $"File not found: {safeFileName}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting pattern file: {FileName}", fileName);
            return StatusCode(500, new { error = "Failed to delete pattern file" });
        }
    }

    // HttpContext wrapper methods for WebConfigurationService
    public async Task ReloadPatternFilesHttpContext(HttpContext context)
    {
        var result = await ReloadPatternFiles();
        
        context.Response.ContentType = "application/json";
        
        if (result.Result is ObjectResult objectResult)
        {
            context.Response.StatusCode = objectResult.StatusCode ?? 200;
            var json = System.Text.Json.JsonSerializer.Serialize(objectResult.Value);
            await context.Response.WriteAsync(json);
        }
        else if (result.Value != null)
        {
            context.Response.StatusCode = 200;
            var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
            await context.Response.WriteAsync(json);
        }
        else
        {
            context.Response.StatusCode = 500;
            var errorJson = System.Text.Json.JsonSerializer.Serialize(new { error = "Unknown error occurred" });
            await context.Response.WriteAsync(errorJson);
        }
    }

    public async Task GetPatternPacks(HttpContext context)
    {
        var result = GetAllPatternPacks();
        
        context.Response.ContentType = "application/json";
        
        if (result.Result is ObjectResult objectResult)
        {
            context.Response.StatusCode = objectResult.StatusCode ?? 200;
            var json = System.Text.Json.JsonSerializer.Serialize(objectResult.Value);
            await context.Response.WriteAsync(json);
        }
        else if (result.Value != null)
        {
            context.Response.StatusCode = 200;
            var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
            await context.Response.WriteAsync(json);
        }
        else
        {
            context.Response.StatusCode = 500;
            var errorJson = System.Text.Json.JsonSerializer.Serialize(new { error = "Unknown error occurred" });
            await context.Response.WriteAsync(errorJson);
        }
    }

    public async Task ExportPatternPack(HttpContext context)
    {
        try
        {
            var body = await BoundedRequestReader.ReadOrRespondAsync(context, "Request body is empty");
            if (body == null)
            {
                return;
            }

            if (!BoundedRequestReader.TryDeserialize<ExportPatternRequest>(body, out var request) || request == null)
            {
                await BoundedRequestReader.WriteErrorAsync(
                    context, StatusCodes.Status400BadRequest, "Request body is not valid JSON");
                return;
            }

            var result = await ExportPatternPack(request);
            
            context.Response.ContentType = "application/json";
            
            if (result.Result is ObjectResult objectResult)
            {
                context.Response.StatusCode = objectResult.StatusCode ?? 200;
                var json = System.Text.Json.JsonSerializer.Serialize(objectResult.Value);
                await context.Response.WriteAsync(json);
            }
            else if (result.Value != null)
            {
                context.Response.StatusCode = 200;
                var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
                await context.Response.WriteAsync(json);
            }
            else
            {
                context.Response.StatusCode = 500;
                var errorJson = System.Text.Json.JsonSerializer.Serialize(new { error = "Unknown error occurred" });
                await context.Response.WriteAsync(errorJson);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting pattern pack");
            await ApiError.WriteAsync(context, 500, "Failed to export pattern pack");
        }
    }

    public async Task ImportPatternFile(HttpContext context)
    {
        try
        {
            // This would need multipart form handling for file upload
            // For now, return not implemented
            context.Response.StatusCode = 501;
            context.Response.ContentType = "application/json";
            var errorJson = System.Text.Json.JsonSerializer.Serialize(new { error = "Import not yet implemented via HttpContext" });
            await context.Response.WriteAsync(errorJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing pattern file");
            await ApiError.WriteAsync(context, 500, "Failed to import pattern file");
        }
    }
}

// DTOs
public class PatternPacksResponse
{
    public List<PatternPackInfo> Packs { get; set; } = new();
    public PatternFileStats Stats { get; set; } = new();
}

public class ShipPatternsResponse
{
    public Dictionary<string, List<ShipPatternSummary>> Ships { get; set; } = new();
    public int TotalShipTypes { get; set; }
    public int TotalDefinitions { get; set; }
}

public class ShipTypePatterns
{
    public string ShipType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public List<string> AvailablePacks { get; set; } = new();
    public Dictionary<string, List<EventPatternSource>> EventPatterns { get; set; } = new();
    public int TotalPatterns { get; set; }
}

public class EventPatternOptions
{
    public string ShipType { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public HapticPattern? RecommendedPattern { get; set; }
    public List<EventPatternSource> AvailablePatterns { get; set; } = new();
    public string? PreferredPack { get; set; }
}

public class ShipPatternSummary
{
    public string ShipType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string PackName { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public int EventCount { get; set; }
    public List<string> Events { get; set; } = new();
    public List<string> Tags { get; set; } = new();
}

public class EventPatternSource
{
    public HapticPattern Pattern { get; set; } = new();
    public string PackName { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
}

public class PatternFileStats
{
    public int TotalPacks { get; set; }
    public int TotalShips { get; set; }
    public int TotalPatterns { get; set; }
    public List<string> Authors { get; set; } = new();
    public List<string> Tags { get; set; } = new();
}

public class ExportPatternRequest
{
    public string PackName { get; set; } = string.Empty;
    public List<string> ShipTypes { get; set; } = new();
}

public class PatternExportResponse
{
    public string Message { get; set; } = string.Empty;
    public string ExportPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int ShipCount { get; set; }
    public DateTime Created { get; set; }
}

public class PatternReloadResponse
{
    public string Message { get; set; } = string.Empty;
    public int TotalPacks { get; set; }
    public int NewPacks { get; set; }
    public List<string> PackNames { get; set; } = new();
    public DateTime ReloadTimestamp { get; set; }
}