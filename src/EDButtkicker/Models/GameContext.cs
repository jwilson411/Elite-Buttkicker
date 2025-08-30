using System.Text.Json.Serialization;

namespace EDButtkicker.Models;

public enum GameState
{
    Unknown,
    MainMenu,
    InSystem,
    InSupercruise,
    Docked,
    Landed,
    InCombat,
    Interdiction,
    Exploration,
    Trading,
    Mining,
    SRVMode,
    FighterMode
}

public enum CombatThreatLevel
{
    None,
    Low,        // Single enemy, shields up
    Medium,     // Multiple enemies or shields down
    High,       // Hull damage or overwhelming force
    Critical    // Near death or emergency situation
}

public enum ExplorationMode
{
    None,
    SystemScanning,
    PlanetScanning,
    SignalInvestigation,
    FirstDiscovery,
    LongRangeExploration
}

public class GameContext
{
    public GameState CurrentState { get; set; } = GameState.Unknown;
    public DateTime LastStateChange { get; set; } = DateTime.UtcNow;
    public TimeSpan StateActivityDuration => DateTime.UtcNow - LastStateChange;
    
    // Location Context
    public string? CurrentSystem { get; set; }
    public string? CurrentStation { get; set; }
    public string? CurrentBody { get; set; }
    public bool IsInPopulatedSystem { get; set; }
    public bool IsInDangerousSystem { get; set; }
    
    // Ship Status
    public double HullIntegrity { get; set; } = 1.0;
    public double ShieldStrength { get; set; } = 1.0;
    public double FuelLevel { get; set; } = 1.0;
    public bool IsLandingGearDeployed { get; set; }
    public bool IsHardpointsDeployed { get; set; }
    public bool IsFSDCharging { get; set; }
    public bool IsUnderAttack { get; set; }
    
    // Combat Context
    public CombatThreatLevel ThreatLevel { get; set; } = CombatThreatLevel.None;
    public int EnemyCount { get; set; }
    public DateTime? LastCombatActivity { get; set; }
    public double CombatIntensity { get; set; } // 0.0 - 1.0 scale
    
    // Exploration Context
    public ExplorationMode ExplorationActivity { get; set; } = ExplorationMode.None;
    public int SystemsVisited { get; set; }
    public int BodiesScanned { get; set; }
    public bool HasUnscannedBodies { get; set; }
    public double DistanceFromBubble { get; set; }
    
    // Trading Context
    public bool IsCarryingCargo { get; set; }
    public int CargoValue { get; set; }
    public bool IsInTradingRoute { get; set; }
    
    // Behavioral Patterns
    public Dictionary<string, int> RecentEventFrequency { get; set; } = new();
    public Dictionary<GameState, TimeSpan> StateTimeSpent { get; set; } = new();
    public double PlayerAggressiveness { get; set; } // Based on combat engagement patterns
    public double PlayerCautiousness { get; set; } // Based on risk-taking behavior
    
    // Predictive Context
    public GameState? PredictedNextState { get; set; }
    public double PredictionConfidence { get; set; }
    public List<string> LikelyUpcomingEvents { get; set; } = new();
    
    public void UpdateState(GameState newState)
    {
        if (CurrentState != newState)
        {
            // Update state time tracking
            if (StateTimeSpent.ContainsKey(CurrentState))
                StateTimeSpent[CurrentState] += StateActivityDuration;
            else
                StateTimeSpent[CurrentState] = StateActivityDuration;
            
            CurrentState = newState;
            LastStateChange = DateTime.UtcNow;
        }
    }
    
    public void IncrementEventFrequency(string eventType)
    {
        if (RecentEventFrequency.ContainsKey(eventType))
            RecentEventFrequency[eventType]++;
        else
            RecentEventFrequency[eventType] = 1;
    }
    
    public bool IsInDangerousSituation()
    {
        return ThreatLevel >= CombatThreatLevel.Medium ||
               HullIntegrity < 0.5 ||
               (IsUnderAttack && ShieldStrength < 0.3) ||
               (CurrentState == GameState.Interdiction);
    }
    
    public bool IsInRoutineActivity()
    {
        return CurrentState is GameState.Docked or GameState.InSupercruise &&
               StateActivityDuration > TimeSpan.FromMinutes(2) &&
               ThreatLevel == CombatThreatLevel.None;
    }
    
    public double GetContextualIntensityMultiplier()
    {
        var multiplier = 1.0;
        
        // Combat intensity scaling
        switch (ThreatLevel)
        {
            case CombatThreatLevel.Low: multiplier *= 1.1; break;
            case CombatThreatLevel.Medium: multiplier *= 1.3; break;
            case CombatThreatLevel.High: multiplier *= 1.6; break;
            case CombatThreatLevel.Critical: multiplier *= 2.0; break;
        }
        
        // Hull damage urgency
        if (HullIntegrity < 0.5) multiplier *= 1.4;
        if (HullIntegrity < 0.25) multiplier *= 1.8;
        
        // Exploration excitement
        if (ExplorationActivity == ExplorationMode.FirstDiscovery)
            multiplier *= 1.2;
        
        // Routine activity dampening
        if (IsInRoutineActivity())
            multiplier *= 0.7;
            
        return Math.Min(multiplier, 2.5); // Cap at 2.5x
    }
}