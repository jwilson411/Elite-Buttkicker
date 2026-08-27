using Microsoft.Extensions.Logging;

namespace EDButtkicker.Services;

/// <summary>
/// The file-system pattern catalog, as far as the reconciler is concerned.
/// Implemented by <see cref="PatternFileService"/>.
/// </summary>
public interface IPatternCatalog
{
    List<string> GetAllShipTypes();

    List<ShipPatternDefinition> GetPatternsForShip(string shipType);
}

public class PatternSourceRegistration
{
    public string ShipType { get; init; } = string.Empty;
    public string EventName { get; init; } = string.Empty;
    public PatternSourceInfo SourceInfo { get; init; } = new();
}

/// <summary>
/// Registers every pattern the catalog knows about as a selectable source and drops
/// selections whose source has gone away. Runs at startup, whenever pattern files change,
/// and from the refresh-sources API.
/// </summary>
public class PatternSourceCatalogReconciler
{
    private readonly ILogger<PatternSourceCatalogReconciler> _logger;
    private readonly IPatternCatalog _catalog;
    private readonly PatternSelectionService _patternSelectionService;
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);

    public PatternSourceCatalogReconciler(
        ILogger<PatternSourceCatalogReconciler> logger,
        IPatternCatalog catalog,
        PatternSelectionService patternSelectionService)
    {
        _logger = logger;
        _catalog = catalog;
        _patternSelectionService = patternSelectionService;
    }

    /// <summary>
    /// Every ship/event pattern the catalog currently exposes. Driven by
    /// <see cref="IPatternCatalog.GetAllShipTypes"/> - never a hardcoded ship list.
    /// </summary>
    public static List<PatternSourceRegistration> BuildSources(IPatternCatalog catalog)
    {
        var registrations = new List<PatternSourceRegistration>();

        foreach (var shipType in catalog.GetAllShipTypes())
        {
            foreach (var shipPattern in catalog.GetPatternsForShip(shipType))
            {
                foreach (var eventEntry in shipPattern.Events)
                {
                    var eventName = eventEntry.Key;
                    var pattern = eventEntry.Value;

                    registrations.Add(new PatternSourceRegistration
                    {
                        ShipType = shipType,
                        EventName = eventName,
                        SourceInfo = new PatternSourceInfo
                        {
                            SourceId = GenerateSourceId(PatternSourceType.FileSystem, shipPattern.PackName, shipType, eventName),
                            SourceName = $"{shipPattern.PackName} - {shipPattern.DisplayName}",
                            SourceType = PatternSourceType.FileSystem,
                            PackName = shipPattern.PackName,
                            Author = shipPattern.Author,
                            Version = shipPattern.Version,
                            LastModified = DateTime.UtcNow,
                            Description = "",
                            Tags = shipPattern.Tags,
                            PatternType = pattern.Pattern.ToString(),
                            Frequency = pattern.Frequency,
                            Intensity = pattern.Intensity,
                            Duration = pattern.Duration
                        }
                    });
                }
            }
        }

        return registrations;
    }

    /// <summary>
    /// Registers all catalog sources, cleans up selections whose source disappeared and
    /// persists the result. Returns the number of registered sources.
    /// </summary>
    public async Task<int> ReconcileAsync()
    {
        await _reconcileLock.WaitAsync();

        try
        {
            var registrations = BuildSources(_catalog);
            var sourceIds = new HashSet<string>();

            foreach (var registration in registrations)
            {
                sourceIds.Add(registration.SourceInfo.SourceId);
                _patternSelectionService.RegisterPatternSource(registration.ShipType, registration.EventName, registration.SourceInfo);
            }

            _patternSelectionService.CleanupMissingSources(sourceIds);
            await _patternSelectionService.SaveSelectionsAsync();

            _logger.LogInformation("Reconciled {SourceCount} pattern sources from the pattern catalog", sourceIds.Count);

            return sourceIds.Count;
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    public static string GenerateSourceId(PatternSourceType sourceType, string packName, string shipType, string eventName)
    {
        return $"{sourceType}:{packName}:{shipType}:{eventName}".ToLowerInvariant();
    }
}
