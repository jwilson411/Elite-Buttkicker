using Microsoft.Extensions.Logging;
using EDButtkicker.Configuration;
using EDButtkicker.Models;
using System.Text.Json;

namespace EDButtkicker.Services;

public class ContextualIntelligenceService
{
    private readonly ILogger<ContextualIntelligenceService> _logger;
    private readonly AppSettings _settings;
    private readonly GameContext _gameContext;
    private readonly Dictionary<string, DateTime> _eventHistory = new();
    private readonly Dictionary<string, int> _eventPatterns = new();
    private DateTime _lastAnalysisUpdate = DateTime.UtcNow;

    // Configuration
    public bool IsEnabled => _settings.ContextualIntelligence?.Enabled ?? false;
    public double LearningRate => _settings.ContextualIntelligence?.LearningRate ?? 0.1;
    public double PredictionThreshold => _settings.ContextualIntelligence?.PredictionThreshold ?? 0.7;
    public bool EnableAdaptiveIntensity => _settings.ContextualIntelligence?.EnableAdaptiveIntensity ?? true;
    public bool EnablePredictivePatterns => _settings.ContextualIntelligence?.EnablePredictivePatterns ?? true;
    public bool EnableContextualVoice => _settings.ContextualIntelligence?.EnableContextualVoice ?? true;

    public ContextualIntelligenceService(
        ILogger<ContextualIntelligenceService> logger,
        AppSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _gameContext = new GameContext();
        
        _logger.LogInformation("Contextual Intelligence Service initialized - Enabled: {Enabled}", IsEnabled);
    }

    public GameContext GetCurrentContext() => _gameContext;

    public void ProcessEvent(JournalEvent journalEvent)
    {
        if (!IsEnabled) return;

        try
        {
            _logger.LogDebug("Processing event for contextual analysis: {EventType}", journalEvent.Event);
            
            // Update event history and patterns
            UpdateEventHistory(journalEvent);
            
            // Analyze and update game context
            AnalyzeGameState(journalEvent);
            UpdateCombatContext(journalEvent);
            UpdateExplorationContext(journalEvent);
            UpdateTradingContext(journalEvent);
            
            // Perform behavioral analysis
            AnalyzeBehavioralPatterns(journalEvent);
            
            // Update predictions
            UpdatePredictions();
            
            _logger.LogDebug("Context updated - State: {State}, Threat: {Threat}, Intensity: {Intensity}x", 
                _gameContext.CurrentState, _gameContext.ThreatLevel, _gameContext.GetContextualIntensityMultiplier());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing event for contextual intelligence: {EventType}", journalEvent.Event);
        }
    }

    public HapticPattern GetContextuallyAdjustedPattern(HapticPattern originalPattern, JournalEvent? journalEvent = null)
    {
        if (!IsEnabled || !EnableAdaptiveIntensity)
            return originalPattern;

        try
        {
            var adjustedPattern = ClonePattern(originalPattern);
            var contextMultiplier = _gameContext.GetContextualIntensityMultiplier();
            
            // Apply contextual intensity adjustment
            adjustedPattern.Intensity = (int)Math.Min(
                adjustedPattern.Intensity * contextMultiplier,
                _settings.Audio.MaxIntensity);
            
            // Adjust duration for urgent situations
            if (_gameContext.IsInDangerousSituation())
            {
                adjustedPattern.Duration = (int)(adjustedPattern.Duration * 1.2);
                adjustedPattern.FadeOut = Math.Max(adjustedPattern.FadeOut / 2, 50);
            }
            
            // Extend patterns for routine activities to add variety
            if (_gameContext.IsInRoutineActivity())
            {
                adjustedPattern.Duration = (int)(adjustedPattern.Duration * 0.8);
                adjustedPattern.FadeIn *= 2;
                adjustedPattern.FadeOut *= 2;
            }
            
            // Adjust frequency for combat context
            if (_gameContext.CurrentState == GameState.InCombat)
            {
                adjustedPattern.Frequency += (int)(_gameContext.CombatIntensity * 10);
                adjustedPattern.Frequency = Math.Min(adjustedPattern.Frequency, 80);
            }
            
            _logger.LogDebug("Pattern adjusted contextually - Original: {OrigIntensity}%, New: {NewIntensity}%, Multiplier: {Multiplier}x",
                originalPattern.Intensity, adjustedPattern.Intensity, contextMultiplier);
                
            return adjustedPattern;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adjusting pattern contextually, using original");
            return originalPattern;
        }
    }

    public string? GetContextualVoiceMessage(string eventType, JournalEvent? journalEvent = null)
    {
        if (!IsEnabled || !EnableContextualVoice)
            return null;

        try
        {
            return eventType switch
            {
                "HullDamage" when _gameContext.ThreatLevel >= CombatThreatLevel.High => 
                    GetCriticalDamageMessage(journalEvent),
                "FSDJump" when _gameContext.ExplorationActivity == ExplorationMode.FirstDiscovery => 
                    "Entering uncharted system. Scanning for valuable discoveries.",
                "Docked" when _gameContext.IsCarryingCargo && _gameContext.CargoValue > 1000000 => 
                    "High-value cargo delivered safely. Excellent profit margin achieved.",
                "UnderAttack" when _gameContext.ThreatLevel == CombatThreatLevel.Critical => 
                    "Critical threat detected! Evasive maneuvers recommended immediately!",
                "ShieldsUp" when _gameContext.HullIntegrity < 0.3 => 
                    "Shields restored. Hull integrity critical - seek immediate repairs.",
                "Interdicted" when _gameContext.IsCarryingCargo => 
                    "Interdiction detected! Cargo at risk - prepare for evasion or combat.",
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating contextual voice message");
            return null;
        }
    }

    public List<string> GetPredictedUpcomingEvents()
    {
        if (!IsEnabled || !EnablePredictivePatterns)
            return new List<string>();

        return _gameContext.LikelyUpcomingEvents.ToList();
    }

    private void UpdateEventHistory(JournalEvent journalEvent)
    {
        _eventHistory[journalEvent.Event] = journalEvent.Timestamp;
        _gameContext.IncrementEventFrequency(journalEvent.Event);
        
        // Clean old event frequency data (keep last 100 events per type)
        if (_gameContext.RecentEventFrequency.Values.Sum() > 1000)
        {
            var oldestEvents = _gameContext.RecentEventFrequency
                .Where(kvp => kvp.Value < 2)
                .Select(kvp => kvp.Key)
                .ToList();
            
            foreach (var eventType in oldestEvents)
                _gameContext.RecentEventFrequency.Remove(eventType);
        }
    }

    private void AnalyzeGameState(JournalEvent journalEvent)
    {
        var newState = journalEvent.Event switch
        {
            "StartUp" or "LoadGame" => GameState.MainMenu,
            "SupercruiseEntry" => GameState.InSupercruise,
            "SupercruiseExit" => GameState.InSystem,
            "Docked" => GameState.Docked,
            "Undocked" => GameState.InSystem,
            "Touchdown" => GameState.Landed,
            "Liftoff" => GameState.InSystem,
            "LaunchSRV" => GameState.SRVMode,
            "DockSRV" => GameState.Landed,
            "LaunchFighter" or "VehicleSwitch" when journalEvent.AdditionalData?.ContainsKey("To") == true &&
                journalEvent.AdditionalData["To"]?.ToString()?.Contains("Fighter") == true => GameState.FighterMode,
            "UnderAttack" or "HullDamage" => GameState.InCombat,
            "Interdicted" or "Interdiction" => GameState.Interdiction,
            _ => _gameContext.CurrentState
        };

        if (newState != _gameContext.CurrentState)
        {
            _logger.LogDebug("Game state changing from {OldState} to {NewState}", _gameContext.CurrentState, newState);
            _gameContext.UpdateState(newState);
        }

        // Update location context
        if (!string.IsNullOrEmpty(journalEvent.StarSystem))
            _gameContext.CurrentSystem = journalEvent.StarSystem;
        
        if (!string.IsNullOrEmpty(journalEvent.StationName))
            _gameContext.CurrentStation = journalEvent.StationName;

        // Update ship status
        if (journalEvent.Health.HasValue)
            _gameContext.HullIntegrity = journalEvent.Health.Value;
    }

    private void UpdateCombatContext(JournalEvent journalEvent)
    {
        switch (journalEvent.Event)
        {
            case "UnderAttack":
                _gameContext.IsUnderAttack = true;
                _gameContext.LastCombatActivity = DateTime.UtcNow;
                _gameContext.CombatIntensity = Math.Min(_gameContext.CombatIntensity + 0.2, 1.0);
                break;
                
            case "HullDamage":
                var threatLevel = (_gameContext.HullIntegrity, _gameContext.ShieldStrength) switch
                {
                    (< 0.25, _) => CombatThreatLevel.Critical,
                    (< 0.5, < 0.3) => CombatThreatLevel.High,
                    (_, < 0.5) => CombatThreatLevel.Medium,
                    _ => CombatThreatLevel.Low
                };
                _gameContext.ThreatLevel = threatLevel;
                _gameContext.LastCombatActivity = DateTime.UtcNow;
                break;
                
            case "ShieldsUp":
                _gameContext.ShieldStrength = 1.0;
                if (_gameContext.ThreatLevel > CombatThreatLevel.Low)
                    _gameContext.ThreatLevel = CombatThreatLevel.Low;
                break;
                
            case "ShieldDown":
                _gameContext.ShieldStrength = 0.0;
                _gameContext.ThreatLevel = (CombatThreatLevel)Math.Max((byte)_gameContext.ThreatLevel, (byte)CombatThreatLevel.Medium);
                break;
                
            case "FighterDestroyed" or "ShipTargeted":
                _gameContext.EnemyCount = Math.Max(_gameContext.EnemyCount - 1, 0);
                break;
        }

        // Combat cooldown
        if (_gameContext.LastCombatActivity.HasValue &&
            DateTime.UtcNow - _gameContext.LastCombatActivity.Value > TimeSpan.FromMinutes(2))
        {
            _gameContext.IsUnderAttack = false;
            _gameContext.ThreatLevel = CombatThreatLevel.None;
            _gameContext.CombatIntensity *= 0.9; // Gradual decay
        }
    }

    private void UpdateExplorationContext(JournalEvent journalEvent)
    {
        switch (journalEvent.Event)
        {
            case "FSSDiscoveryScan":
                _gameContext.ExplorationActivity = ExplorationMode.SystemScanning;
                break;
                
            case "Scan" when journalEvent.AdditionalData?.ContainsKey("BodyName") == true:
                _gameContext.ExplorationActivity = ExplorationMode.PlanetScanning;
                _gameContext.BodiesScanned++;
                break;
                
            case "CodexEntry":
                _gameContext.ExplorationActivity = ExplorationMode.SignalInvestigation;
                break;
                
            case "FSDJump":
                _gameContext.SystemsVisited++;
                if (journalEvent.AdditionalData?.ContainsKey("Population") == true)
                {
                    var population = journalEvent.AdditionalData["Population"]?.ToString();
                    _gameContext.IsInPopulatedSystem = !string.IsNullOrEmpty(population) && population != "0";
                }
                break;
        }
    }

    private void UpdateTradingContext(JournalEvent journalEvent)
    {
        switch (journalEvent.Event)
        {
            case "MarketBuy":
                _gameContext.IsCarryingCargo = true;
                if (journalEvent.AdditionalData?.ContainsKey("TotalCost") == true &&
                    int.TryParse(journalEvent.AdditionalData["TotalCost"]?.ToString(), out int cost))
                {
                    _gameContext.CargoValue += cost;
                }
                break;
                
            case "MarketSell":
                if (journalEvent.AdditionalData?.ContainsKey("TotalSale") == true &&
                    int.TryParse(journalEvent.AdditionalData["TotalSale"]?.ToString(), out int sale))
                {
                    _gameContext.CargoValue = Math.Max(_gameContext.CargoValue - sale, 0);
                }
                break;
        }
    }

    private void AnalyzeBehavioralPatterns(JournalEvent journalEvent)
    {
        // Analyze player aggressiveness based on combat engagement
        if (journalEvent.Event == "UnderAttack")
        {
            _gameContext.PlayerAggressiveness += LearningRate;
        }
        else if (journalEvent.Event == "FSDJump" && _gameContext.IsUnderAttack)
        {
            // Player fled from combat
            _gameContext.PlayerCautiousness += LearningRate;
            _gameContext.PlayerAggressiveness -= LearningRate * 0.5;
        }

        // Normalize behavioral scores
        _gameContext.PlayerAggressiveness = Math.Max(0, Math.Min(1, _gameContext.PlayerAggressiveness));
        _gameContext.PlayerCautiousness = Math.Max(0, Math.Min(1, _gameContext.PlayerCautiousness));
    }

    private void UpdatePredictions()
    {
        if (!EnablePredictivePatterns || DateTime.UtcNow - _lastAnalysisUpdate < TimeSpan.FromSeconds(30))
            return;

        _lastAnalysisUpdate = DateTime.UtcNow;
        
        // Predict next state based on current context
        _gameContext.PredictedNextState = _gameContext.CurrentState switch
        {
            GameState.Docked when _gameContext.StateActivityDuration > TimeSpan.FromMinutes(5) => GameState.InSystem,
            GameState.InSupercruise when _gameContext.HasUnscannedBodies => GameState.InSystem,
            GameState.InSystem when _gameContext.IsCarryingCargo => GameState.Docked,
            GameState.InCombat when _gameContext.HullIntegrity < 0.3 => GameState.InSupercruise,
            _ => null
        };

        // Predict likely upcoming events
        _gameContext.LikelyUpcomingEvents.Clear();
        
        if (_gameContext.CurrentState == GameState.Docked)
            _gameContext.LikelyUpcomingEvents.Add("Undocked");
            
        if (_gameContext.IsInDangerousSituation())
            _gameContext.LikelyUpcomingEvents.AddRange(new[] { "HullDamage", "ShieldDown", "UnderAttack" });
            
        if (_gameContext.CurrentState == GameState.InSupercruise && _gameContext.HasUnscannedBodies)
            _gameContext.LikelyUpcomingEvents.Add("SupercruiseExit");
    }

    private string GetCriticalDamageMessage(JournalEvent? journalEvent)
    {
        var hullPercent = (int)(_gameContext.HullIntegrity * 100);
        
        if (hullPercent < 15)
            return "CRITICAL HULL BREACH! Emergency repairs required immediately!";
        else if (hullPercent < 30)
            return $"Severe hull damage detected! Hull integrity at {hullPercent}%. Seek repairs!";
        else
            return $"Hull damage sustained. Integrity at {hullPercent}%.";
    }

    private HapticPattern ClonePattern(HapticPattern original)
    {
        // Create a deep copy of the pattern for modification
        var json = JsonSerializer.Serialize(original);
        return JsonSerializer.Deserialize<HapticPattern>(json) ?? original;
    }
}

public class ContextualIntelligenceConfiguration
{
    public bool Enabled { get; set; } = false;
    public double LearningRate { get; set; } = 0.1;
    public double PredictionThreshold { get; set; } = 0.7;
    public bool EnableAdaptiveIntensity { get; set; } = true;
    public bool EnablePredictivePatterns { get; set; } = true;
    public bool EnableContextualVoice { get; set; } = true;
    public bool LogContextAnalysis { get; set; } = false;
}