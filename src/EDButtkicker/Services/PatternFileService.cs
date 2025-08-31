using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using EDButtkicker.Models;
using System.IO;

namespace EDButtkicker.Services;

public class PatternFileService
{
    private readonly ILogger<PatternFileService> _logger;
    private readonly string _patternsPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly FileSystemWatcher _fileWatcher;
    
    private Dictionary<string, PatternFile> _loadedFiles = new();
    private Dictionary<string, List<ShipPatternDefinition>> _shipPatterns = new();
    private readonly object _lock = new object();

    public event Action<PatternFileChangeEventArgs>? PatternFilesChanged;

    public PatternFileService(ILogger<PatternFileService> logger)
    {
        _logger = logger;
        
        // Use patterns directory in application root
        _patternsPath = Path.Combine(Directory.GetCurrentDirectory(), "patterns");
        Directory.CreateDirectory(_patternsPath);
        
        // Ensure Custom directory exists for user patterns
        var customPath = Path.Combine(_patternsPath, "Custom");
        Directory.CreateDirectory(customPath);
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            AllowTrailingCommas = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        // Set up file watcher for automatic reloading
        _fileWatcher = new FileSystemWatcher(_patternsPath, "*.json")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.FileName
        };
        
        _fileWatcher.Changed += OnFileChanged;
        _fileWatcher.Created += OnFileChanged;
        _fileWatcher.Deleted += OnFileDeleted;
        _fileWatcher.Renamed += OnFileRenamed;
        _fileWatcher.EnableRaisingEvents = true;

        _logger.LogInformation("PatternFileService initialized with path: {PatternsPath}", _patternsPath);
    }

    public async Task LoadAllPatternsAsync()
    {
        try
        {
            _logger.LogInformation("Loading pattern files from {PatternsPath}", _patternsPath);
            
            var jsonFiles = Directory.GetFiles(_patternsPath, "*.json", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith(".") && Path.GetFileName(f) != "schema.json")
                .ToList();

            var loadedCount = 0;
            var errorCount = 0;

            lock (_lock)
            {
                _loadedFiles.Clear();
                _shipPatterns.Clear();
            }

            foreach (var filePath in jsonFiles)
            {
                try
                {
                    var patternFile = await LoadPatternFileAsync(filePath);
                    if (patternFile != null)
                    {
                        lock (_lock)
                        {
                            var relativePath = Path.GetRelativePath(_patternsPath, filePath);
                            _loadedFiles[relativePath] = patternFile;
                            
                            // Index patterns by ship type
                            foreach (var ship in patternFile.Ships)
                            {
                                if (!_shipPatterns.ContainsKey(ship.Key))
                                {
                                    _shipPatterns[ship.Key] = new List<ShipPatternDefinition>();
                                }
                                
                                _shipPatterns[ship.Key].Add(new ShipPatternDefinition
                                {
                                    ShipType = ship.Key,
                                    DisplayName = ship.Value.DisplayName ?? ship.Key,
                                    Class = ship.Value.Class ?? "medium",
                                    Role = ship.Value.Role ?? "multipurpose",
                                    Events = ship.Value.Events ?? new Dictionary<string, HapticPattern>(),
                                    SourceFile = relativePath,
                                    PackName = patternFile.Metadata.Name,
                                    Author = patternFile.Metadata.Author,
                                    Version = patternFile.Metadata.Version,
                                    Tags = patternFile.Metadata.Tags ?? new List<string>()
                                });
                            }
                        }
                        loadedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error loading pattern file: {FilePath}", filePath);
                    errorCount++;
                }
            }

            var totalShips = _shipPatterns.Values.Sum(list => list.Count);
            var totalPatterns = _shipPatterns.Values.SelectMany(list => list).Sum(ship => ship.Events.Count);
            
            _logger.LogInformation("Loaded {LoadedFiles} pattern files with {Ships} ship definitions and {Patterns} total patterns. {Errors} errors.",
                loadedCount, totalShips, totalPatterns, errorCount);
                
            NotifyPatternFilesChanged(PatternFileChangeType.Reload, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading pattern files");
        }
    }

    private async Task<PatternFile?> LoadPatternFileAsync(string filePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var patternFile = JsonSerializer.Deserialize<PatternFile>(json, _jsonOptions);
            
            if (patternFile?.Metadata == null)
            {
                _logger.LogWarning("Pattern file missing metadata: {FilePath}", filePath);
                return null;
            }

            if (patternFile.Ships == null || !patternFile.Ships.Any())
            {
                _logger.LogWarning("Pattern file contains no ships: {FilePath}", filePath);
                return null;
            }

            _logger.LogDebug("Loaded pattern file '{Name}' v{Version} by {Author} with {ShipCount} ships",
                patternFile.Metadata.Name, patternFile.Metadata.Version, patternFile.Metadata.Author,
                patternFile.Ships.Count);

            return patternFile;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Invalid JSON in pattern file: {FilePath}", filePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading pattern file: {FilePath}", filePath);
            return null;
        }
    }

    public List<ShipPatternDefinition> GetPatternsForShip(string shipType)
    {
        lock (_lock)
        {
            var normalizedShipType = shipType.ToLowerInvariant().Replace(" ", "").Replace("_", "");
            
            // Try exact match first
            if (_shipPatterns.TryGetValue(shipType.ToLowerInvariant(), out var exact))
            {
                return new List<ShipPatternDefinition>(exact);
            }
            
            // Try normalized match
            var normalized = _shipPatterns.FirstOrDefault(kv => 
                kv.Key.ToLowerInvariant().Replace(" ", "").Replace("_", "") == normalizedShipType);
            
            if (normalized.Value != null)
            {
                return new List<ShipPatternDefinition>(normalized.Value);
            }
            
            return new List<ShipPatternDefinition>();
        }
    }

    public HapticPattern? GetPatternForShipEvent(string shipType, string eventName, string? preferredPack = null)
    {
        var shipPatterns = GetPatternsForShip(shipType);
        
        if (!shipPatterns.Any())
        {
            return null;
        }

        // If preferred pack specified, try that first
        if (!string.IsNullOrEmpty(preferredPack))
        {
            var preferredPattern = shipPatterns
                .Where(p => p.PackName.Equals(preferredPack, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(p => p.Events.ContainsKey(eventName));
                
            if (preferredPattern != null && preferredPattern.Events.TryGetValue(eventName, out var pattern))
            {
                return pattern;
            }
        }

        // Otherwise, get first available pattern for this event
        foreach (var shipPattern in shipPatterns.OrderBy(p => p.PackName))
        {
            if (shipPattern.Events.TryGetValue(eventName, out var eventPattern))
            {
                return eventPattern;
            }
        }

        return null;
    }

    public List<PatternPackInfo> GetAllPatternPacks()
    {
        lock (_lock)
        {
            return _loadedFiles.Select(kv => new PatternPackInfo
            {
                FilePath = kv.Key,
                Name = kv.Value.Metadata.Name,
                Version = kv.Value.Metadata.Version,
                Author = kv.Value.Metadata.Author,
                Description = kv.Value.Metadata.Description ?? "",
                Tags = kv.Value.Metadata.Tags ?? new List<string>(),
                ShipCount = kv.Value.Ships.Count,
                PatternCount = kv.Value.Ships.Values.Sum(ship => ship.Events?.Count ?? 0),
                Created = kv.Value.Metadata.Created,
                IsValid = true
            }).OrderBy(p => p.Name).ToList();
        }
    }

    public List<string> GetAllShipTypes()
    {
        lock (_lock)
        {
            return _shipPatterns.Keys.OrderBy(k => k).ToList();
        }
    }

    public async Task<string> ExportPatternPackAsync(string packName, List<string> shipTypes)
    {
        try
        {
            var exportData = new PatternFile
            {
                Metadata = new PatternFileMetadata
                {
                    Name = packName,
                    Version = "1.0.0",
                    Author = "User Export",
                    Description = $"Exported pattern pack: {packName}",
                    Tags = new List<string> { "export", "custom" },
                    Created = DateTime.UtcNow,
                    Compatibility = "1.0.0"
                },
                Ships = new Dictionary<string, ShipPatternData>()
            };

            lock (_lock)
            {
                foreach (var shipType in shipTypes)
                {
                    var shipPatterns = GetPatternsForShip(shipType);
                    var firstPattern = shipPatterns.FirstOrDefault();
                    
                    if (firstPattern != null)
                    {
                        exportData.Ships[shipType] = new ShipPatternData
                        {
                            DisplayName = firstPattern.DisplayName,
                            Class = firstPattern.Class,
                            Role = firstPattern.Role,
                            Events = firstPattern.Events
                        };
                    }
                }
            }

            var json = JsonSerializer.Serialize(exportData, _jsonOptions);
            var exportPath = Path.Combine(_patternsPath, "exports", $"{packName}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            
            Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
            await File.WriteAllTextAsync(exportPath, json);
            
            _logger.LogInformation("Exported pattern pack '{PackName}' with {ShipCount} ships to {ExportPath}",
                packName, shipTypes.Count, exportPath);
                
            return exportPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting pattern pack: {PackName}", packName);
            throw;
        }
    }

    public async Task<bool> ImportPatternFileAsync(string sourceFilePath, string? targetFileName = null)
    {
        try
        {
            // Validate the file first
            var patternFile = await LoadPatternFileAsync(sourceFilePath);
            if (patternFile == null)
            {
                return false;
            }

            var fileName = targetFileName ?? Path.GetFileName(sourceFilePath);
            var targetPath = Path.Combine(_patternsPath, "imports", fileName);
            
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourceFilePath, targetPath, overwrite: true);
            
            _logger.LogInformation("Imported pattern file '{Name}' to {TargetPath}", 
                patternFile.Metadata.Name, targetPath);
                
            // File watcher will trigger reload automatically
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing pattern file: {SourcePath}", sourceFilePath);
            return false;
        }
    }

    private async void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            // Debounce file changes
            await Task.Delay(500);
            
            if (Path.GetExtension(e.FullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Pattern file changed: {FilePath}", e.FullPath);
                await ReloadPatternFileAsync(e.FullPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling file change: {FilePath}", e.FullPath);
        }
    }

    private async void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (Path.GetExtension(e.FullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Pattern file deleted: {FilePath}", e.FullPath);
                RemovePatternFile(e.FullPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling file deletion: {FilePath}", e.FullPath);
        }
    }

    private async void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            if (Path.GetExtension(e.FullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Pattern file renamed: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);
                RemovePatternFile(e.OldFullPath);
                await ReloadPatternFileAsync(e.FullPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling file rename: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);
        }
    }

    private async Task ReloadPatternFileAsync(string fullPath)
    {
        try
        {
            var relativePath = Path.GetRelativePath(_patternsPath, fullPath);
            
            // Remove old version
            RemovePatternFile(fullPath);
            
            // Load new version
            var patternFile = await LoadPatternFileAsync(fullPath);
            if (patternFile != null)
            {
                lock (_lock)
                {
                    _loadedFiles[relativePath] = patternFile;
                    
                    foreach (var ship in patternFile.Ships)
                    {
                        if (!_shipPatterns.ContainsKey(ship.Key))
                        {
                            _shipPatterns[ship.Key] = new List<ShipPatternDefinition>();
                        }
                        
                        _shipPatterns[ship.Key].Add(new ShipPatternDefinition
                        {
                            ShipType = ship.Key,
                            DisplayName = ship.Value.DisplayName ?? ship.Key,
                            Class = ship.Value.Class ?? "medium",
                            Role = ship.Value.Role ?? "multipurpose", 
                            Events = ship.Value.Events ?? new Dictionary<string, HapticPattern>(),
                            SourceFile = relativePath,
                            PackName = patternFile.Metadata.Name,
                            Author = patternFile.Metadata.Author,
                            Version = patternFile.Metadata.Version,
                            Tags = patternFile.Metadata.Tags ?? new List<string>()
                        });
                    }
                }
                
                NotifyPatternFilesChanged(PatternFileChangeType.Updated, relativePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reloading pattern file: {FilePath}", fullPath);
        }
    }

    private void RemovePatternFile(string fullPath)
    {
        try
        {
            var relativePath = Path.GetRelativePath(_patternsPath, fullPath);
            
            lock (_lock)
            {
                if (_loadedFiles.TryGetValue(relativePath, out var removedFile))
                {
                    _loadedFiles.Remove(relativePath);
                    
                    // Remove ship patterns from this file
                    foreach (var shipType in removedFile.Ships.Keys)
                    {
                        if (_shipPatterns.TryGetValue(shipType, out var shipPatternList))
                        {
                            shipPatternList.RemoveAll(p => p.SourceFile == relativePath);
                            if (!shipPatternList.Any())
                            {
                                _shipPatterns.Remove(shipType);
                            }
                        }
                    }
                    
                    NotifyPatternFilesChanged(PatternFileChangeType.Deleted, relativePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing pattern file: {FilePath}", fullPath);
        }
    }

    private void NotifyPatternFilesChanged(PatternFileChangeType changeType, string? filePath)
    {
        try
        {
            PatternFilesChanged?.Invoke(new PatternFileChangeEventArgs
            {
                ChangeType = changeType,
                FilePath = filePath,
                TotalFiles = _loadedFiles.Count,
                TotalShips = _shipPatterns.Count,
                TotalPatterns = _shipPatterns.Values.SelectMany(list => list).Sum(ship => ship.Events.Count)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error notifying pattern file changes");
        }
    }

    public void Dispose()
    {
        _fileWatcher?.Dispose();
    }
}

// Supporting classes
public class PatternFile
{
    public PatternFileMetadata Metadata { get; set; } = new();
    public Dictionary<string, ShipPatternData> Ships { get; set; } = new();
}

public class PatternFileMetadata  
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string>? Tags { get; set; }
    public DateTime? Created { get; set; }
    public string? Compatibility { get; set; }
}

public class ShipPatternData
{
    public string? DisplayName { get; set; }
    public string? Class { get; set; }
    public string? Role { get; set; }
    public Dictionary<string, HapticPattern>? Events { get; set; }
}

public class ShipPatternDefinition
{
    public string ShipType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public Dictionary<string, HapticPattern> Events { get; set; } = new();
    public string SourceFile { get; set; } = string.Empty;
    public string PackName { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
}

public class PatternPackInfo
{
    public string FilePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public int ShipCount { get; set; }
    public int PatternCount { get; set; }
    public DateTime? Created { get; set; }
    public bool IsValid { get; set; }
}

public class PatternFileChangeEventArgs
{
    public PatternFileChangeType ChangeType { get; set; }
    public string? FilePath { get; set; }
    public int TotalFiles { get; set; }
    public int TotalShips { get; set; }
    public int TotalPatterns { get; set; }
}

public enum PatternFileChangeType
{
    Created,
    Updated, 
    Deleted,
    Reload
}