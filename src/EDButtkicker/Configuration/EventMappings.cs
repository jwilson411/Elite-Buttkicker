using EDButtkicker.Models;

namespace EDButtkicker.Configuration;

public class EventMappingsConfig
{
    public Dictionary<string, EventMapping> EventMappings { get; set; } = new();
    
    public static EventMappingsConfig GetDefault()
    {
        return new EventMappingsConfig
        {
            EventMappings = new Dictionary<string, EventMapping>
            {
                ["FSDJump"] = new EventMapping
                {
                    EventType = "FSDJump",
                    Pattern = new HapticPattern
                    {
                        Name = "Hyperspace Jump",
                        Pattern = PatternType.MultiLayer,
                        Frequency = 35,
                        Duration = 3000,
                        Intensity = 90,
                        FadeIn = 500,
                        FadeOut = 1000,
                        IntensityCurve = IntensityCurve.Exponential,
                        EnableVoiceAnnouncement = true,
                        VoiceMessage = "Hyperspace jump initiated",
                        Layers = new List<PatternLayer>
                        {
                            new PatternLayer { Waveform = WaveformType.Sine, Frequency = 35, Amplitude = 0.7f, Curve = IntensityCurve.Exponential },
                            new PatternLayer { Waveform = WaveformType.Sine, Frequency = 70, Amplitude = 0.3f, Curve = IntensityCurve.Linear, PhaseOffset = 90 }
                        }
                    },
                    Enabled = true
                },
                ["Docked"] = new EventMapping
                {
                    EventType = "Docked",
                    Pattern = new HapticPattern
                    {
                        Name = "Station Docking",
                        Pattern = PatternType.Impact,
                        Frequency = 45,
                        Duration = 800,
                        Intensity = 70,
                        FadeIn = 50,
                        FadeOut = 300
                    },
                    Enabled = true
                },
                ["Undocked"] = new EventMapping
                {
                    EventType = "Undocked",
                    Pattern = new HapticPattern
                    {
                        Name = "Station Undocking",
                        Pattern = PatternType.Fade,
                        Frequency = 40,
                        Duration = 500,
                        Intensity = 60,
                        FadeIn = 100,
                        FadeOut = 200
                    },
                    Enabled = true
                },
                ["HullDamage"] = new EventMapping
                {
                    EventType = "HullDamage",
                    Pattern = new HapticPattern
                    {
                        Name = "Hull Damage",
                        Pattern = PatternType.SharpPulse,
                        Frequency = 50,
                        Duration = 200,
                        Intensity = 80,
                        IntensityFromDamage = true,
                        MaxIntensity = 100,
                        MinIntensity = 30,
                        IntensityCurve = IntensityCurve.Bounce,
                        EnableVoiceAnnouncement = true,
                        VoiceMessage = "Hull integrity at {health} percent",
                        Conditions = new Dictionary<string, object>
                        {
                            ["health_below"] = 0.5 // Only announce if health below 50%
                        }
                    },
                    Enabled = true
                },
                ["ShipTargeted"] = new EventMapping
                {
                    EventType = "ShipTargeted",
                    Pattern = new HapticPattern
                    {
                        Name = "Target Lock",
                        Pattern = PatternType.SharpPulse,
                        Frequency = 60,
                        Duration = 150,
                        Intensity = 40
                    },
                    Enabled = true
                },
                ["FighterDestroyed"] = new EventMapping
                {
                    EventType = "FighterDestroyed",
                    Pattern = new HapticPattern
                    {
                        Name = "Explosion",
                        Pattern = PatternType.Impact,
                        Frequency = 30,
                        Duration = 1000,
                        Intensity = 95,
                        FadeIn = 0,
                        FadeOut = 600
                    },
                    Enabled = true
                },
                
                // Planetary Landing Events
                ["Touchdown"] = new EventMapping
                {
                    EventType = "Touchdown",
                    Pattern = new HapticPattern
                    {
                        Name = "Planetary Landing",
                        Pattern = PatternType.Impact,
                        Frequency = 25,
                        Duration = 1200,
                        Intensity = 75,
                        FadeIn = 100,
                        FadeOut = 400
                    },
                    Enabled = true
                },
                ["Liftoff"] = new EventMapping
                {
                    EventType = "Liftoff",
                    Pattern = new HapticPattern
                    {
                        Name = "Planetary Takeoff",
                        Pattern = PatternType.BuildupRumble,
                        Frequency = 30,
                        Duration = 2000,
                        Intensity = 65,
                        FadeIn = 300,
                        FadeOut = 500
                    },
                    Enabled = true
                },
                
                // Heat Warning
                ["HeatWarning"] = new EventMapping
                {
                    EventType = "HeatWarning",
                    Pattern = new HapticPattern
                    {
                        Name = "Overheating Warning",
                        Pattern = PatternType.Oscillating,
                        Frequency = 55,
                        Duration = 1500,
                        Intensity = 60,
                        FadeIn = 200,
                        FadeOut = 200
                    },
                    Enabled = true
                },
                ["HeatDamage"] = new EventMapping
                {
                    EventType = "HeatDamage",
                    Pattern = new HapticPattern
                    {
                        Name = "Heat Damage",
                        Pattern = PatternType.Oscillating,
                        Frequency = 65,
                        Duration = 800,
                        Intensity = 85,
                        FadeIn = 50,
                        FadeOut = 200
                    },
                    Enabled = true
                },
                
                // Fuel Scooping
                ["FuelScoop"] = new EventMapping
                {
                    EventType = "FuelScoop",
                    Pattern = new HapticPattern
                    {
                        Name = "Fuel Scooping",
                        Pattern = PatternType.SustainedRumble,
                        Frequency = 35,
                        Duration = 2500,
                        Intensity = 50,
                        FadeIn = 400,
                        FadeOut = 600
                    },
                    Enabled = true
                },
                
                // Combat Events
                ["UnderAttack"] = new EventMapping
                {
                    EventType = "UnderAttack",
                    Pattern = new HapticPattern
                    {
                        Name = "Under Attack",
                        Pattern = PatternType.SharpPulse,
                        Frequency = 70,
                        Duration = 300,
                        Intensity = 95,
                        FadeIn = 0,
                        FadeOut = 100
                    },
                    Enabled = true
                },
                
                // Fighter Bay Events
                ["LaunchFighter"] = new EventMapping
                {
                    EventType = "LaunchFighter",
                    Pattern = new HapticPattern
                    {
                        Name = "Fighter Launch",
                        Pattern = PatternType.BuildupRumble,
                        Frequency = 40,
                        Duration = 1500,
                        Intensity = 60,
                        FadeIn = 200,
                        FadeOut = 400
                    },
                    Enabled = true
                },
                ["DockFighter"] = new EventMapping
                {
                    EventType = "DockFighter",
                    Pattern = new HapticPattern
                    {
                        Name = "Fighter Docking",
                        Pattern = PatternType.Impact,
                        Frequency = 45,
                        Duration = 600,
                        Intensity = 55,
                        FadeIn = 100,
                        FadeOut = 200
                    },
                    Enabled = true
                },
                
                // Neutron Star Boost
                ["JetConeBoost"] = new EventMapping
                {
                    EventType = "JetConeBoost",
                    Pattern = new HapticPattern
                    {
                        Name = "Neutron Boost",
                        Pattern = PatternType.Oscillating,
                        Frequency = 25,
                        Duration = 3000,
                        Intensity = 80,
                        FadeIn = 500,
                        FadeOut = 800
                    },
                    Enabled = true
                },
                
                // Interdiction Events
                ["Interdicted"] = new EventMapping
                {
                    EventType = "Interdicted",
                    Pattern = new HapticPattern
                    {
                        Name = "Being Interdicted",
                        Pattern = PatternType.Oscillating,
                        Frequency = 45,
                        Duration = 4000,
                        Intensity = 85,
                        FadeIn = 300,
                        FadeOut = 500
                    },
                    Enabled = true
                },
                ["Interdiction"] = new EventMapping
                {
                    EventType = "Interdiction",
                    Pattern = new HapticPattern
                    {
                        Name = "Interdicting Target",
                        Pattern = PatternType.BuildupRumble,
                        Frequency = 40,
                        Duration = 3500,
                        Intensity = 75,
                        FadeIn = 400,
                        FadeOut = 600
                    },
                    Enabled = true
                },
                
                // Additional Combat/Damage Events
                ["ShieldDown"] = new EventMapping
                {
                    EventType = "ShieldDown",
                    Pattern = new HapticPattern
                    {
                        Name = "Shields Down",
                        Pattern = PatternType.Impact,
                        Frequency = 35,
                        Duration = 1000,
                        Intensity = 90,
                        FadeIn = 50,
                        FadeOut = 400
                    },
                    Enabled = true
                },
                ["ShieldsUp"] = new EventMapping
                {
                    EventType = "ShieldsUp",
                    Pattern = new HapticPattern
                    {
                        Name = "Shields Online",
                        Pattern = PatternType.BuildupRumble,
                        Frequency = 50,
                        Duration = 800,
                        Intensity = 60,
                        FadeIn = 200,
                        FadeOut = 300,
                        EnableVoiceAnnouncement = true,
                        VoiceMessage = "Shields are online"
                    },
                    Enabled = true
                },
                
                // Advanced Pattern Examples
                ["CriticalDamageSequence"] = new EventMapping
                {
                    EventType = "HullDamage", // This would be triggered conditionally
                    Pattern = new HapticPattern
                    {
                        Name = "Critical Damage Sequence",
                        Pattern = PatternType.Sequence,
                        Frequency = 60,
                        Duration = 500,
                        Intensity = 100,
                        ChainedPatterns = new List<string> { "Warning Pulse", "Emergency Alert" },
                        Conditions = new Dictionary<string, object>
                        {
                            ["health_below"] = 0.25 // Only for critical health
                        },
                        EnableVoiceAnnouncement = true,
                        VoiceMessage = "Critical hull damage! Seek immediate repairs!",
                        IntensityCurve = IntensityCurve.Exponential
                    },
                    Enabled = false // Disabled by default - advanced users can enable
                }
            }
        };
    }
}