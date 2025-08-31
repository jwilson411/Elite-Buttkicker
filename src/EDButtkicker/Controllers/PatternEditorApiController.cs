using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using EDButtkicker.Services;
using EDButtkicker.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EDButtkicker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PatternEditorController : ControllerBase
{
    private readonly ILogger<PatternEditorController> _logger;
    private readonly PatternFileService _patternFileService;
    private readonly AudioEngineService _audioEngineService;

    public PatternEditorController(
        ILogger<PatternEditorController> logger,
        PatternFileService patternFileService,
        AudioEngineService audioEngineService)
    {
        _logger = logger;
        _patternFileService = patternFileService;
        _audioEngineService = audioEngineService;
    }

    [HttpGet("templates")]
    public ActionResult<PatternTemplatesResponse> GetPatternTemplates()
    {
        try
        {
            var templates = new PatternTemplatesResponse
            {
                BasicPatterns = new List<PatternTemplate>
                {
                    new() { 
                        Name = "Simple Pulse", 
                        Pattern = "SharpPulse", 
                        Description = "Basic pulse pattern for quick events",
                        DefaultSettings = new HapticPattern 
                        { 
                            Pattern = PatternType.SharpPulse, 
                            Frequency = 40, 
                            Intensity = 70, 
                            Duration = 500 
                        }
                    },
                    new() { 
                        Name = "Impact", 
                        Pattern = "Impact", 
                        Description = "Sharp impact for collisions and damage",
                        DefaultSettings = new HapticPattern 
                        { 
                            Pattern = PatternType.Impact, 
                            Frequency = 45, 
                            Intensity = 85, 
                            Duration = 300,
                            FadeOut = 200
                        }
                    },
                    new() { 
                        Name = "Sustained Rumble", 
                        Pattern = "SustainedRumble", 
                        Description = "Continuous rumble for ongoing events",
                        DefaultSettings = new HapticPattern 
                        { 
                            Pattern = PatternType.SustainedRumble, 
                            Frequency = 35, 
                            Intensity = 60, 
                            Duration = 2000,
                            FadeIn = 300,
                            FadeOut = 500
                        }
                    },
                    new() { 
                        Name = "Buildup Rumble", 
                        Pattern = "BuildupRumble", 
                        Description = "Growing intensity for jump sequences",
                        DefaultSettings = new HapticPattern 
                        { 
                            Pattern = PatternType.BuildupRumble, 
                            Frequency = 38, 
                            Intensity = 80, 
                            Duration = 3000,
                            FadeIn = 600,
                            FadeOut = 800,
                            IntensityCurve = IntensityCurve.Linear
                        }
                    },
                    new() { 
                        Name = "Oscillating", 
                        Pattern = "Oscillating", 
                        Description = "Pulsing pattern for warnings",
                        DefaultSettings = new HapticPattern 
                        { 
                            Pattern = PatternType.Oscillating, 
                            Frequency = 42, 
                            Intensity = 75, 
                            Duration = 1500,
                            FadeOut = 400
                        }
                    }
                },
                ShipTypes = GetAvailableShipTypes(),
                EventTypes = GetAvailableEventTypes()
            };

            return Ok(templates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting pattern templates");
            return StatusCode(500, new { error = "Failed to get pattern templates", details = ex.Message });
        }
    }

    [HttpPost("create")]
    public ActionResult<CreatePatternResponse> CreateNewPattern([FromBody] CreatePatternRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.PackName) || string.IsNullOrEmpty(request.Author))
            {
                return BadRequest(new { error = "Pack name and author are required" });
            }

            // Validate pack name format
            if (!IsValidPackName(request.PackName))
            {
                return BadRequest(new { 
                    error = "Invalid pack name. Use only letters, numbers, spaces, underscores, and hyphens" 
                });
            }

            // Generate safe filename
            var safeFileName = GenerateSafeFileName(request.PackName, request.Author);
            
            var newPattern = new PatternFileDefinition
            {
                Metadata = new PatternFileMetadata
                {
                    Name = request.PackName,
                    Version = "1.0.0",
                    Author = request.Author,
                    Description = request.Description ?? $"Custom patterns by {request.Author}",
                    Tags = request.Tags ?? new List<string> { "custom", "user-created" },
                    Created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    Compatibility = "1.0.0"
                },
                Ships = new Dictionary<string, ShipPatternDefinition>()
            };

            // Add initial ship if specified
            if (!string.IsNullOrEmpty(request.InitialShipType))
            {
                var shipDef = new ShipPatternDefinition
                {
                    DisplayName = request.InitialShipDisplayName ?? request.InitialShipType,
                    Class = DetermineShipClass(request.InitialShipType),
                    Role = DetermineShipRole(request.InitialShipType),
                    Events = new Dictionary<string, HapticPattern>()
                };

                newPattern.Ships[request.InitialShipType] = shipDef;
            }

            return Ok(new CreatePatternResponse
            {
                PatternFile = newPattern,
                SafeFileName = safeFileName,
                SuggestedPath = Path.Combine("Custom", safeFileName)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating new pattern");
            return StatusCode(500, new { error = "Failed to create new pattern", details = ex.Message });
        }
    }

    [HttpPost("save")]
    public async Task<ActionResult<SavePatternResponse>> SavePattern([FromBody] SavePatternRequest request)
    {
        try
        {
            if (request.PatternFile?.Metadata == null)
            {
                return BadRequest(new { error = "Pattern file data is required" });
            }

            // Generate safe filename if not provided
            var fileName = request.FileName;
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = GenerateSafeFileName(
                    request.PatternFile.Metadata.Name, 
                    request.PatternFile.Metadata.Author);
            }

            // Ensure .json extension
            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                fileName += ".json";
            }

            // Determine save location
            var saveDirectory = request.SaveToCustom ? "Custom" : "patterns";
            var fullPath = Path.Combine(Directory.GetCurrentDirectory(), "patterns", saveDirectory, fileName);

            // Create directory if it doesn't exist
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Update metadata
            request.PatternFile.Metadata.LastModified = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            
            // Save to file
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var json = JsonSerializer.Serialize(request.PatternFile, jsonOptions);
            await System.IO.File.WriteAllTextAsync(fullPath, json);

            // Reload patterns to include the new file
            await _patternFileService.LoadAllPatternsAsync();

            return Ok(new SavePatternResponse
            {
                Message = $"Pattern file '{fileName}' saved successfully",
                FileName = fileName,
                FilePath = fullPath,
                SaveLocation = saveDirectory,
                Created = System.IO.File.GetCreationTime(fullPath),
                FileSize = new FileInfo(fullPath).Length
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving pattern file");
            return StatusCode(500, new { error = "Failed to save pattern file", details = ex.Message });
        }
    }

    [HttpGet("load/{fileName}")]
    public async Task<ActionResult<PatternFileDefinition>> LoadPatternForEditing(string fileName)
    {
        try
        {
            // Try to find the file in various locations
            var searchPaths = new[]
            {
                Path.Combine("patterns", "Custom", fileName),
                Path.Combine("patterns", "Community", fileName),
                Path.Combine("patterns", "Small_Ships", fileName),
                Path.Combine("patterns", "Large_Ships", fileName),
                Path.Combine("patterns", fileName)
            };

            foreach (var searchPath in searchPaths)
            {
                var fullPath = Path.Combine(Directory.GetCurrentDirectory(), searchPath);
                if (System.IO.File.Exists(fullPath))
                {
                    var json = await System.IO.File.ReadAllTextAsync(fullPath);
                    var patternFile = JsonSerializer.Deserialize<PatternFileDefinition>(json, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        PropertyNameCaseInsensitive = true
                    });

                    return Ok(patternFile);
                }
            }

            return NotFound(new { error = $"Pattern file '{fileName}' not found" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading pattern file for editing: {FileName}", fileName);
            return StatusCode(500, new { error = "Failed to load pattern file", details = ex.Message });
        }
    }

    [HttpPost("validate")]
    public ActionResult<ValidationResponse> ValidatePattern([FromBody] PatternFileDefinition patternFile)
    {
        try
        {
            var validation = new ValidationResponse
            {
                IsValid = true,
                Errors = new List<string>(),
                Warnings = new List<string>()
            };

            // Validate metadata
            if (patternFile.Metadata == null)
            {
                validation.Errors.Add("Metadata is required");
            }
            else
            {
                if (string.IsNullOrEmpty(patternFile.Metadata.Name))
                    validation.Errors.Add("Pack name is required");
                if (string.IsNullOrEmpty(patternFile.Metadata.Author))
                    validation.Errors.Add("Author is required");
                if (string.IsNullOrEmpty(patternFile.Metadata.Version))
                    validation.Warnings.Add("Version not specified, will default to 1.0.0");
            }

            // Validate ships
            if (patternFile.Ships == null || !patternFile.Ships.Any())
            {
                validation.Warnings.Add("No ships defined in pattern file");
            }
            else
            {
                foreach (var ship in patternFile.Ships)
                {
                    if (string.IsNullOrEmpty(ship.Value.DisplayName))
                        validation.Warnings.Add($"Ship '{ship.Key}' has no display name");
                    
                    if (ship.Value.Events == null || !ship.Value.Events.Any())
                        validation.Warnings.Add($"Ship '{ship.Key}' has no events defined");
                    
                    // Validate patterns
                    if (ship.Value.Events != null)
                    {
                        foreach (var eventPattern in ship.Value.Events)
                        {
                            var pattern = eventPattern.Value;
                            if (pattern.Frequency < 10 || pattern.Frequency > 100)
                                validation.Warnings.Add($"Ship '{ship.Key}' event '{eventPattern.Key}': Frequency should be between 10-100Hz");
                            if (pattern.Intensity < 1 || pattern.Intensity > 100)
                                validation.Errors.Add($"Ship '{ship.Key}' event '{eventPattern.Key}': Intensity must be between 1-100%");
                            if (pattern.Duration < 50 || pattern.Duration > 10000)
                                validation.Warnings.Add($"Ship '{ship.Key}' event '{eventPattern.Key}': Duration should be between 50-10000ms");
                        }
                    }
                }
            }

            validation.IsValid = !validation.Errors.Any();

            return Ok(validation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating pattern file");
            return StatusCode(500, new { error = "Failed to validate pattern file", details = ex.Message });
        }
    }

    [HttpPost("test")]
    public async Task<ActionResult> TestPattern([FromBody] TestPatternRequest request)
    {
        try
        {
            if (request.Pattern == null)
            {
                return BadRequest(new { error = "Pattern is required for testing" });
            }

            // Test the pattern by playing it
            await _audioEngineService.PlayHapticPattern(request.Pattern);

            return Ok(new { message = "Pattern test started successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing pattern");
            return StatusCode(500, new { error = "Failed to test pattern", details = ex.Message });
        }
    }

    [HttpGet("user-files/{author}")]
    public ActionResult<UserFilesResponse> GetUserFiles(string author)
    {
        try
        {
            var customPath = Path.Combine(Directory.GetCurrentDirectory(), "patterns", "Custom");
            var userFiles = new List<UserPatternFile>();

            if (Directory.Exists(customPath))
            {
                var files = Directory.GetFiles(customPath, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var json = System.IO.File.ReadAllText(file);
                        var pattern = JsonSerializer.Deserialize<PatternFileDefinition>(json, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            PropertyNameCaseInsensitive = true
                        });

                        if (pattern?.Metadata != null && 
                            string.Equals(pattern.Metadata.Author, author, StringComparison.OrdinalIgnoreCase))
                        {
                            var fileInfo = new FileInfo(file);
                            userFiles.Add(new UserPatternFile
                            {
                                FileName = Path.GetFileName(file),
                                PackName = pattern.Metadata.Name,
                                Author = pattern.Metadata.Author,
                                Description = pattern.Metadata.Description,
                                Version = pattern.Metadata.Version,
                                Tags = pattern.Metadata.Tags,
                                ShipCount = pattern.Ships?.Count ?? 0,
                                EventCount = pattern.Ships?.Values.Sum(s => s.Events?.Count ?? 0) ?? 0,
                                Created = fileInfo.CreationTime,
                                LastModified = fileInfo.LastWriteTime,
                                FileSize = fileInfo.Length
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse pattern file: {File}", file);
                    }
                }
            }

            return Ok(new UserFilesResponse
            {
                Author = author,
                Files = userFiles.OrderByDescending(f => f.LastModified).ToList(),
                TotalFiles = userFiles.Count,
                TotalShips = userFiles.Sum(f => f.ShipCount),
                TotalEvents = userFiles.Sum(f => f.EventCount)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user files for author: {Author}", author);
            return StatusCode(500, new { error = "Failed to get user files", details = ex.Message });
        }
    }

    private static bool IsValidPackName(string packName)
    {
        return Regex.IsMatch(packName, @"^[a-zA-Z0-9\s\-_]+$");
    }

    private static string GenerateSafeFileName(string packName, string author)
    {
        // Clean pack name and author
        var safePack = Regex.Replace(packName, @"[^\w\s\-]", "").Replace(" ", "_");
        var safeAuthor = Regex.Replace(author, @"[^\w\s\-]", "").Replace(" ", "_");
        
        // Generate filename: Author_PackName_timestamp
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        return $"{safeAuthor}_{safePack}_{timestamp}.json";
    }

    private static string DetermineShipClass(string shipType)
    {
        // This should ideally use the ship classifications from your existing system
        var smallShips = new[] { "sidewinder", "eagle", "hauler", "adder", "viper", "cobramkiii", "viper_mkiv", "diamondback_scout", "imperial_courier" };
        var largeShips = new[] { "anaconda", "cutter", "corvette", "belugaliner", "type9", "type10" };
        
        if (smallShips.Contains(shipType.ToLowerInvariant()))
            return "small";
        if (largeShips.Contains(shipType.ToLowerInvariant()))
            return "large";
        return "medium";
    }

    private static string DetermineShipRole(string shipType)
    {
        // This should ideally use the ship classifications from your existing system
        var combatShips = new[] { "sidewinder", "eagle", "viper", "vulture", "fer_de_lance", "corvette" };
        var explorerShips = new[] { "asp", "diamondback_explorer", "anaconda", "krait_phantom" };
        var traderShips = new[] { "hauler", "type6", "type7", "type9", "type10", "cutter" };
        
        var shipLower = shipType.ToLowerInvariant();
        if (combatShips.Contains(shipLower))
            return "combat";
        if (explorerShips.Contains(shipLower))
            return "exploration";
        if (traderShips.Contains(shipLower))
            return "trading";
        return "multipurpose";
    }

    private List<string> GetAvailableShipTypes()
    {
        // This should come from your ship classification system
        return new List<string>
        {
            "sidewinder", "eagle", "hauler", "adder", "viper", "cobramkiii", "viper_mkiv",
            "diamondback_scout", "imperial_courier", "vulture", "asp", "diamondback_explorer",
            "imperial_clipper", "fer_de_lance", "keelback", "type6", "dolphin", "krait_mkii",
            "krait_phantom", "mamba", "type7", "alliance_chieftain", "alliance_challenger",
            "alliance_crusader", "python", "type9", "anaconda", "federation_corvette",
            "imperial_cutter", "belugaliner", "type10"
        };
    }

    private List<string> GetAvailableEventTypes()
    {
        return new List<string>
        {
            "FSDJump", "Docked", "Undocked", "HullDamage", "ShieldDown", "ShieldsUp",
            "UnderAttack", "ShipTargeted", "FighterDestroyed", "TouchDown", "Liftoff",
            "HeatWarning", "HeatDamage", "FuelScoop", "LaunchFighter", "DockFighter",
            "JetConeBoost", "Interdicted", "Interdiction", "CargoScoop", "Discovery"
        };
    }
}

// DTOs for pattern editing
public class PatternTemplatesResponse
{
    public List<PatternTemplate> BasicPatterns { get; set; } = new();
    public List<string> ShipTypes { get; set; } = new();
    public List<string> EventTypes { get; set; } = new();
}

public class PatternTemplate
{
    public string Name { get; set; } = string.Empty;
    public string Pattern { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public HapticPattern DefaultSettings { get; set; } = new();
}

public class CreatePatternRequest
{
    public string PackName { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string>? Tags { get; set; }
    public string? InitialShipType { get; set; }
    public string? InitialShipDisplayName { get; set; }
}

public class CreatePatternResponse
{
    public PatternFileDefinition PatternFile { get; set; } = new();
    public string SafeFileName { get; set; } = string.Empty;
    public string SuggestedPath { get; set; } = string.Empty;
}

public class SavePatternRequest
{
    public PatternFileDefinition PatternFile { get; set; } = new();
    public string? FileName { get; set; }
    public bool SaveToCustom { get; set; } = true;
}

public class SavePatternResponse
{
    public string Message { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string SaveLocation { get; set; } = string.Empty;
    public DateTime Created { get; set; }
    public long FileSize { get; set; }
}

public class ValidationResponse
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public class TestPatternRequest
{
    public HapticPattern Pattern { get; set; } = new();
}

public class UserFilesResponse
{
    public string Author { get; set; } = string.Empty;
    public List<UserPatternFile> Files { get; set; } = new();
    public int TotalFiles { get; set; }
    public int TotalShips { get; set; }
    public int TotalEvents { get; set; }
}

public class UserPatternFile
{
    public string FileName { get; set; } = string.Empty;
    public string PackName { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public int ShipCount { get; set; }
    public int EventCount { get; set; }
    public DateTime Created { get; set; }
    public DateTime LastModified { get; set; }
    public long FileSize { get; set; }
}

public class PatternFileDefinition
{
    public PatternFileMetadata Metadata { get; set; } = new();
    public Dictionary<string, ShipPatternDefinition> Ships { get; set; } = new();
}

public class PatternFileMetadata
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public string Created { get; set; } = string.Empty;
    public string? LastModified { get; set; }
    public string Compatibility { get; set; } = string.Empty;
}

public class ShipPatternDefinition
{
    public string DisplayName { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public Dictionary<string, HapticPattern> Events { get; set; } = new();
}