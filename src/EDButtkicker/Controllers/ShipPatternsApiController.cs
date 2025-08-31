using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using EDButtkicker.Models;
using EDButtkicker.Services;

namespace EDButtkicker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ShipPatternsController : ControllerBase
{
    private readonly ILogger<ShipPatternsController> _logger;
    private readonly ShipPatternService _shipPatternService;
    private readonly ShipTrackingService _shipTrackingService;
    private readonly EventMappingService _eventMappingService;

    public ShipPatternsController(
        ILogger<ShipPatternsController> logger,
        ShipPatternService shipPatternService,
        ShipTrackingService shipTrackingService,
        EventMappingService eventMappingService)
    {
        _logger = logger;
        _shipPatternService = shipPatternService;
        _shipTrackingService = shipTrackingService;
        _eventMappingService = eventMappingService;
    }

    [HttpGet("current-ship")]
    public ActionResult<CurrentShipResponse> GetCurrentShip()
    {
        try
        {
            var currentShip = _shipTrackingService.GetCurrentShip();
            
            if (currentShip == null)
            {
                return Ok(new CurrentShipResponse 
                { 
                    Ship = null, 
                    Message = "No ship currently tracked. Start Elite Dangerous or trigger a ship-related event." 
                });
            }

            var shipPatterns = _shipPatternService.GetShipPatterns(currentShip.GetShipKey());
            var recommendations = _shipPatternService.GetRecommendationsForShip(currentShip.ShipType);

            return Ok(new CurrentShipResponse
            {
                Ship = new ShipInfo
                {
                    ShipKey = currentShip.GetShipKey(),
                    ShipType = currentShip.ShipType,
                    ShipName = currentShip.ShipName,
                    DisplayName = currentShip.GetDisplayName(),
                    ShipID = currentShip.ShipID,
                    LastUpdated = currentShip.LastUpdated,
                    Class = recommendations.ShipSize.ToString(),
                    Role = recommendations.ShipRole.ToString(),
                    CustomPatternCount = shipPatterns?.EventPatterns.Count ?? 0
                },
                Recommendations = recommendations
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting current ship information");
            return StatusCode(500, new { error = "Failed to get current ship information", details = ex.Message });
        }
    }

    [HttpGet]
    public ActionResult<ShipPatternsLibraryResponse> GetAllShipPatterns()
    {
        try
        {
            var allShips = _shipPatternService.GetAllShipPatterns();
            var currentShip = _shipTrackingService.GetCurrentShip();
            var stats = _shipPatternService.GetStats();

            var response = new ShipPatternsLibraryResponse
            {
                Ships = allShips.Select(ship => new ShipInfo
                {
                    ShipKey = ship.ShipKey,
                    ShipType = ship.ShipType,
                    ShipName = ship.ShipName,
                    DisplayName = $"{ship.ShipName} ({ship.ShipType})",
                    LastUpdated = ship.LastModified,
                    CustomPatternCount = ship.EventPatterns.Count,
                    IsActive = ship.IsActive,
                    IsCurrent = currentShip?.GetShipKey() == ship.ShipKey,
                    Class = ShipClassifications.GetShipClass(ship.ShipType).Size.ToString(),
                    Role = ShipClassifications.GetShipClass(ship.ShipType).Role.ToString()
                }).ToList(),
                Stats = stats,
                CurrentShipKey = currentShip?.GetShipKey()
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting ship patterns library");
            return StatusCode(500, new { error = "Failed to get ship patterns library", details = ex.Message });
        }
    }

    [HttpGet("{shipKey}")]
    public ActionResult<ShipDetailResponse> GetShipDetails(string shipKey)
    {
        try
        {
            var shipPatterns = _shipPatternService.GetShipPatterns(shipKey);
            
            if (shipPatterns == null)
            {
                return NotFound(new { error = $"Ship not found: {shipKey}" });
            }

            var defaultPatterns = _eventMappingService.GetAllDefaultPatterns();
            var recommendations = _shipPatternService.GetRecommendationsForShip(shipPatterns.ShipType);
            var currentShip = _shipTrackingService.GetCurrentShip();

            var response = new ShipDetailResponse
            {
                Ship = new ShipInfo
                {
                    ShipKey = shipPatterns.ShipKey,
                    ShipType = shipPatterns.ShipType,
                    ShipName = shipPatterns.ShipName,
                    DisplayName = $"{shipPatterns.ShipName} ({shipPatterns.ShipType})",
                    LastUpdated = shipPatterns.LastModified,
                    CustomPatternCount = shipPatterns.EventPatterns.Count,
                    IsActive = shipPatterns.IsActive,
                    IsCurrent = currentShip?.GetShipKey() == shipKey,
                    Class = recommendations.ShipSize.ToString(),
                    Role = recommendations.ShipRole.ToString()
                },
                Patterns = CreateEventPatternList(shipPatterns, defaultPatterns),
                Recommendations = recommendations
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting ship details for {ShipKey}", shipKey);
            return StatusCode(500, new { error = "Failed to get ship details", details = ex.Message });
        }
    }

    [HttpPost("{shipKey}/patterns")]
    public async Task<ActionResult> SetShipPattern(string shipKey, [FromBody] SetPatternRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.EventName) || request.Pattern == null)
            {
                return BadRequest(new { error = "EventName and Pattern are required" });
            }

            await _shipPatternService.SetShipPatternAsync(shipKey, request.EventName, request.Pattern);
            
            return Ok(new { 
                message = $"Pattern set for {request.EventName} on ship {shipKey}",
                shipKey = shipKey,
                eventName = request.EventName,
                patternName = request.Pattern.Name
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting ship pattern for {ShipKey}", shipKey);
            return StatusCode(500, new { error = "Failed to set ship pattern", details = ex.Message });
        }
    }

    [HttpDelete("{shipKey}/patterns/{eventName}")]
    public async Task<ActionResult> RemoveShipPattern(string shipKey, string eventName)
    {
        try
        {
            await _shipPatternService.RemoveShipPatternAsync(shipKey, eventName);
            
            return Ok(new { 
                message = $"Pattern removed for {eventName} on ship {shipKey}",
                shipKey = shipKey,
                eventName = eventName
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing ship pattern for {ShipKey}", shipKey);
            return StatusCode(500, new { error = "Failed to remove ship pattern", details = ex.Message });
        }
    }

    [HttpPost("{shipKey}/apply-recommendations")]
    public async Task<ActionResult> ApplyRecommendedPatterns(string shipKey)
    {
        try
        {
            var shipPatterns = _shipPatternService.GetShipPatterns(shipKey);
            
            if (shipPatterns == null)
            {
                return NotFound(new { error = $"Ship not found: {shipKey}" });
            }

            await _shipPatternService.ApplyRecommendedPatternsAsync(shipKey, shipPatterns.ShipType);
            
            return Ok(new { 
                message = $"Applied recommended patterns for {shipPatterns.ShipName}",
                shipKey = shipKey,
                shipType = shipPatterns.ShipType
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying recommended patterns for {ShipKey}", shipKey);
            return StatusCode(500, new { error = "Failed to apply recommended patterns", details = ex.Message });
        }
    }

    [HttpDelete("{shipKey}")]
    public async Task<ActionResult> RemoveShip(string shipKey)
    {
        try
        {
            await _shipPatternService.RemoveShipAsync(shipKey);
            
            return Ok(new { 
                message = $"Ship removed: {shipKey}",
                shipKey = shipKey
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing ship {ShipKey}", shipKey);
            return StatusCode(500, new { error = "Failed to remove ship", details = ex.Message });
        }
    }

    [HttpGet("classifications")]
    public ActionResult<ShipClassificationResponse> GetShipClassifications()
    {
        try
        {
            var classifications = ShipClassifications.ShipClasses.Select(kv => new ShipClassificationInfo
            {
                ShipType = kv.Key,
                DisplayName = kv.Key.Replace("_", " ").Replace("mk", "Mk"),
                Class = kv.Value.Name,
                Size = kv.Value.Size,
                Role = kv.Value.Role
            }).OrderBy(s => s.Size).ThenBy(s => s.DisplayName).ToList();

            return Ok(new ShipClassificationResponse
            {
                Classifications = classifications,
                TotalShips = classifications.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting ship classifications");
            return StatusCode(500, new { error = "Failed to get ship classifications", details = ex.Message });
        }
    }

    [HttpGet("{shipKey}/recommendations")]
    public ActionResult<PatternRecommendations> GetShipRecommendations(string shipKey)
    {
        try
        {
            var shipPatterns = _shipPatternService.GetShipPatterns(shipKey);
            
            if (shipPatterns == null)
            {
                return NotFound(new { error = $"Ship not found: {shipKey}" });
            }

            var recommendations = _shipPatternService.GetRecommendationsForShip(shipPatterns.ShipType);
            return Ok(recommendations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recommendations for {ShipKey}", shipKey);
            return StatusCode(500, new { error = "Failed to get ship recommendations", details = ex.Message });
        }
    }

    private List<EventPatternInfo> CreateEventPatternList(ShipSpecificPatterns shipPatterns, Dictionary<string, HapticPattern> defaultPatterns)
    {
        var eventPatterns = new List<EventPatternInfo>();

        // Get all unique event names from both custom and default patterns
        var allEventNames = defaultPatterns.Keys
            .Union(shipPatterns.EventPatterns.Keys)
            .Distinct()
            .OrderBy(e => e)
            .ToList();

        foreach (var eventName in allEventNames)
        {
            var hasCustom = shipPatterns.EventPatterns.TryGetValue(eventName, out var customPattern);
            var hasDefault = defaultPatterns.TryGetValue(eventName, out var defaultPattern);

            var eventPattern = new EventPatternInfo
            {
                EventName = eventName,
                HasCustomPattern = hasCustom,
                HasDefaultPattern = hasDefault,
                CustomPattern = customPattern,
                DefaultPattern = defaultPattern,
                ActivePattern = customPattern ?? defaultPattern
            };

            eventPatterns.Add(eventPattern);
        }

        return eventPatterns;
    }
}

// DTOs for API responses
public class CurrentShipResponse
{
    public ShipInfo? Ship { get; set; }
    public PatternRecommendations? Recommendations { get; set; }
    public string? Message { get; set; }
}

public class ShipPatternsLibraryResponse
{
    public List<ShipInfo> Ships { get; set; } = new();
    public ShipPatternStats Stats { get; set; } = new();
    public string? CurrentShipKey { get; set; }
}

public class ShipDetailResponse
{
    public ShipInfo Ship { get; set; } = new();
    public List<EventPatternInfo> Patterns { get; set; } = new();
    public PatternRecommendations Recommendations { get; set; } = new();
}

public class ShipClassificationResponse
{
    public List<ShipClassificationInfo> Classifications { get; set; } = new();
    public int TotalShips { get; set; }
}

public class ShipInfo
{
    public string ShipKey { get; set; } = string.Empty;
    public string ShipType { get; set; } = string.Empty;
    public string ShipName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public long? ShipID { get; set; }
    public DateTime LastUpdated { get; set; }
    public string Class { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public int CustomPatternCount { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsCurrent { get; set; }
}

public class EventPatternInfo
{
    public string EventName { get; set; } = string.Empty;
    public bool HasCustomPattern { get; set; }
    public bool HasDefaultPattern { get; set; }
    public HapticPattern? CustomPattern { get; set; }
    public HapticPattern? DefaultPattern { get; set; }
    public HapticPattern? ActivePattern { get; set; }
}

public class ShipClassificationInfo
{
    public string ShipType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    public ShipSize Size { get; set; }
    public ShipRole Role { get; set; }
}

public class SetPatternRequest
{
    public string EventName { get; set; } = string.Empty;
    public HapticPattern Pattern { get; set; } = new();
}