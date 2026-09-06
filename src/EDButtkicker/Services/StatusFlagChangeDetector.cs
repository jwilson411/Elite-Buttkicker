namespace EDButtkicker.Services;

/// <summary>
/// Turns one Status.json flag transition into the event names <see cref="EventMappingService"/>
/// already knows about. Pure bit logic - no file access, no audio - so the mapping from a flag edge
/// to an event can be tested without a game install, a watcher or a device.
/// Both words are considered: an edge that only shows up in Flags2 (on foot, environment warnings,
/// glide) is a real edge and produces its event just like a Flags edge does.
/// </summary>
public static class StatusFlagChangeDetector
{
    /// <summary>Status.json "Flags" bits (ship/SRV state).</summary>
    [Flags]
    public enum StatusFlags : long
    {
        Docked              = 1L << 0,   // 1
        Landed              = 1L << 1,   // 2
        LandingGearDown     = 1L << 2,   // 4
        ShieldsUp           = 1L << 3,   // 8
        Supercruise         = 1L << 4,   // 16
        FlightAssistOff     = 1L << 5,   // 32
        HardpointsDeployed  = 1L << 6,   // 64
        InWing              = 1L << 7,   // 128
        LightsOn            = 1L << 8,   // 256
        CargoScoopDeployed  = 1L << 9,   // 512
        SilentRunning       = 1L << 10,  // 1024
        ScoopingFuel        = 1L << 11,  // 2048
        FsdMassLocked       = 1L << 16,  // 65536
        FsdCharging         = 1L << 17,  // 131072
        FsdCooldown         = 1L << 18,  // 262144
        LowFuel             = 1L << 19,  // 524288
        Overheating         = 1L << 20,  // 1048576
        HaveMission         = 1L << 21,
        Interdicted         = 1L << 23,
        InMainShip          = 1L << 24,
        InFighter           = 1L << 25,
        InSRV               = 1L << 26,
        NightVision         = 1L << 28,
    }

    /// <summary>
    /// Status.json "Flags2" bits (Odyssey on-foot, environment and FSD state), per the Elite
    /// Dangerous journal documentation.
    /// </summary>
    [Flags]
    public enum StatusFlags2 : long
    {
        OnFoot                = 1L << 0,
        InTaxi                = 1L << 1,
        InMulticrew           = 1L << 2,
        OnFootInStation       = 1L << 3,
        OnFootOnPlanet        = 1L << 4,
        AimDownSight          = 1L << 5,
        LowOxygen             = 1L << 6,
        LowHealth             = 1L << 7,
        Cold                  = 1L << 8,
        Hot                   = 1L << 9,
        VeryCold              = 1L << 10,
        VeryHot               = 1L << 11,
        GlideMode             = 1L << 12,
        OnFootInHangar        = 1L << 13,
        OnFootSocialSpace     = 1L << 14,
        OnFootExterior        = 1L << 15,
        BreathableAtmosphere  = 1L << 16,
        TelepresenceMulticrew = 1L << 17,
        PhysicalMulticrew     = 1L << 18,
        FsdJump               = 1L << 19,
        FsdCharging           = 1L << 20,
    }

    /// <summary>
    /// The event names produced by the transition, in the order they should be played. An empty
    /// list means nothing haptic-worthy changed. Names with no mapping stay a no-op downstream -
    /// this method does not know or care which ones are mapped.
    /// </summary>
    public static IReadOnlyList<string> Detect(long oldFlags, long newFlags, long oldFlags2, long newFlags2)
    {
        var events = new List<string>();

        var changed = oldFlags ^ newFlags;
        var changed2 = oldFlags2 ^ newFlags2;

        if (changed == 0 && changed2 == 0)
        {
            return events;
        }

        // --- Flags: ship state ---

        // Toggles report both directions, because retracting is as much a mechanical event as
        // deploying is.
        AddToggle(events, changed, newFlags, (long)StatusFlags.LandingGearDown, "LandingGearDown", "LandingGearUp");
        AddToggle(events, changed, newFlags, (long)StatusFlags.HardpointsDeployed, "HardpointsDeployed", "HardpointsRetracted");
        AddToggle(events, changed, newFlags, (long)StatusFlags.CargoScoopDeployed, "CargoScoopDeployed", "CargoScoopRetracted");
        AddToggle(events, changed, newFlags, (long)StatusFlags.SilentRunning, "SilentRunningOn", "SilentRunningOff");
        AddToggle(events, changed, newFlags, (long)StatusFlags.NightVision, "NightVisionOn", "NightVisionOff");

        // Warnings and momentary states only fire as they come on - the bit dropping is the
        // condition ending, which needs no cue.
        AddRising(events, changed, newFlags, (long)StatusFlags.FsdCharging, "FsdCharging");
        AddRising(events, changed, newFlags, (long)StatusFlags.FsdCooldown, "FsdCooldown");
        AddRising(events, changed, newFlags, (long)StatusFlags.LowFuel, "LowFuel");
        AddRising(events, changed, newFlags, (long)StatusFlags.Overheating, "Overheating");

        // --- Flags2: on foot, environment, FSD ---

        AddRising(events, changed2, newFlags2, (long)StatusFlags2.OnFoot, "OnFoot");
        AddRising(events, changed2, newFlags2, (long)StatusFlags2.LowOxygen, "LowOxygen");
        AddRising(events, changed2, newFlags2, (long)StatusFlags2.LowHealth, "LowHealth");
        AddRising(events, changed2, newFlags2, (long)StatusFlags2.Cold, "Cold");
        AddRising(events, changed2, newFlags2, (long)StatusFlags2.VeryCold, "VeryCold");
        AddRising(events, changed2, newFlags2, (long)StatusFlags2.Hot, "Hot");
        AddRising(events, changed2, newFlags2, (long)StatusFlags2.VeryHot, "VeryHot");
        AddRising(events, changed2, newFlags2, (long)StatusFlags2.FsdJump, "FsdJumpInProgress");
        AddToggle(events, changed2, newFlags2, (long)StatusFlags2.GlideMode, "GlideModeOn", "GlideModeOff");

        return events;
    }

    private static void AddToggle(
        List<string> events, long changed, long current, long bit, string onName, string offName)
    {
        if ((changed & bit) == 0) return;

        events.Add((current & bit) != 0 ? onName : offName);
    }

    private static void AddRising(List<string> events, long changed, long current, long bit, string name)
    {
        if ((changed & bit) == 0) return;
        if ((current & bit) == 0) return;

        events.Add(name);
    }
}
