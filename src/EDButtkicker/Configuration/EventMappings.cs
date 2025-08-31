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
                ["StartJump"] = new EventMapping
                {
                    EventType = "StartJump",
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
                        Name = "Landing Gear Lock & Fuel Connection",
                        Pattern = PatternType.Sequence,
                        Frequency = 42,
                        Duration = 4000,
                        Intensity = 55,
                        FadeIn = 5,
                        FadeOut = 300,
                        Layers = new List<PatternLayer>
                        {
                            // 1. Quick hard rumble - landing gear lock (keep strong!)
                            new PatternLayer
                            {
                                Waveform = WaveformType.Square,
                                Frequency = 35,
                                Amplitude = 0.7f,
                                StartTime = 0,
                                Duration = 200,
                                FadeIn = 10,
                                FadeOut = 50,
                                Curve = IntensityCurve.Linear
                            },
                            // 2. Small break (no layer - natural silence from 250ms to 600ms)
                            
                            // 3. Hose connection - now more noticeable
                            new PatternLayer
                            {
                                Waveform = WaveformType.Sine,
                                Frequency = 42,
                                Amplitude = 0.55f, // Boosted from 0.35f
                                StartTime = 600,
                                Duration = 250,
                                FadeIn = 20,
                                FadeOut = 30,
                                Curve = IntensityCurve.Linear
                            },
                            // 4. Gas flowing - much more prominent and satisfying
                            new PatternLayer
                            {
                                Waveform = WaveformType.Sine,
                                Frequency = 38,
                                Amplitude = 0.5f, // Boosted from 0.25f (doubled!)
                                StartTime = 1000,
                                Duration = 2500,
                                FadeIn = 150,
                                FadeOut = 800,
                                Curve = IntensityCurve.Exponential
                            }
                        }
                    },
                    Enabled = true
                },
                ["Undocked"] = new EventMapping
                {
                    EventType = "Undocked",
                    Pattern = new HapticPattern
                    {
                        Name = "Service Disconnection & Gear Release",
                        Pattern = PatternType.Sequence,
                        Frequency = 40,
                        Duration = 2200,
                        Intensity = 50,
                        FadeIn = 50,
                        FadeOut = 600,
                        Layers = new List<PatternLayer>
                        {
                            // Systems shutdown warning (brief alert)
                            new PatternLayer
                            {
                                Waveform = WaveformType.Sine,
                                Frequency = 52,
                                Amplitude = 0.3f,
                                StartTime = 0,
                                Duration = 150,
                                FadeIn = 5,
                                FadeOut = 30,
                                Curve = IntensityCurve.Linear
                            },
                            // Fuel hoses disconnecting (declining rumble)
                            new PatternLayer
                            {
                                Waveform = WaveformType.Sine,
                                Frequency = 42,
                                Amplitude = 0.45f,
                                StartTime = 200,
                                Duration = 1400,
                                FadeIn = 50,
                                FadeOut = 500,
                                Curve = IntensityCurve.Logarithmic
                            },
                            // Maintenance stopping (fading oscillation)
                            new PatternLayer
                            {
                                Waveform = WaveformType.Triangle,
                                Frequency = 30,
                                Amplitude = 0.3f,
                                StartTime = 400,
                                Duration = 1000,
                                FadeIn = 100,
                                FadeOut = 400,
                                Curve = IntensityCurve.Exponential
                            },
                            // Landing gear releasing (final mechanical click)
                            new PatternLayer
                            {
                                Waveform = WaveformType.Square,
                                Frequency = 35,
                                Amplitude = 0.5f,
                                StartTime = 1800,
                                Duration = 160,
                                FadeIn = 10,
                                FadeOut = 40,
                                Curve = IntensityCurve.Linear
                            }
                        }
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
                        Name = "Landing Pad Touchdown Sequence",
                        Pattern = PatternType.Sequence,
                        Frequency = 35,
                        Duration = 2800,
                        Intensity = 50,
                        FadeIn = 5,
                        FadeOut = 400,
                        Layers = new List<PatternLayer>
                        {
                            // 1. Initial touchdown impact (brief sharp contact)
                            new PatternLayer
                            {
                                Waveform = WaveformType.Square,
                                Frequency = 32,
                                Amplitude = 0.6f,
                                StartTime = 0,
                                Duration = 120,
                                FadeIn = 5,
                                FadeOut = 30,
                                Curve = IntensityCurve.Linear
                            },
                            // 2. Landing pad adjustment (ship settling)
                            new PatternLayer
                            {
                                Waveform = WaveformType.Triangle,
                                Frequency = 28,
                                Amplitude = 0.4f,
                                StartTime = 200,
                                Duration = 400,
                                FadeIn = 50,
                                FadeOut = 100,
                                Curve = IntensityCurve.Logarithmic
                            },
                            // 3. Magnetic clamps engaging (mechanical locking)
                            new PatternLayer
                            {
                                Waveform = WaveformType.Square,
                                Frequency = 38,
                                Amplitude = 0.45f,
                                StartTime = 800,
                                Duration = 180,
                                FadeIn = 10,
                                FadeOut = 20,
                                Curve = IntensityCurve.Linear
                            },
                            // 4. Pad systems connecting (gentle service connection)
                            new PatternLayer
                            {
                                Waveform = WaveformType.Sine,
                                Frequency = 35,
                                Amplitude = 0.3f,
                                StartTime = 1200,
                                Duration = 1200,
                                FadeIn = 100,
                                FadeOut = 500,
                                Curve = IntensityCurve.Exponential
                            }
                        }
                    },
                    Enabled = true
                },
                ["Liftoff"] = new EventMapping
                {
                    EventType = "Liftoff",
                    Pattern = new HapticPattern
                    {
                        Name = "Landing Pad Liftoff Sequence",
                        Pattern = PatternType.Sequence,
                        Frequency = 34,
                        Duration = 2400,
                        Intensity = 52,
                        FadeIn = 20,
                        FadeOut = 600,
                        Layers = new List<PatternLayer>
                        {
                            // 1. Service systems disconnecting (brief shutdown)
                            new PatternLayer
                            {
                                Waveform = WaveformType.Sine,
                                Frequency = 36,
                                Amplitude = 0.35f,
                                StartTime = 0,
                                Duration = 200,
                                FadeIn = 20,
                                FadeOut = 40,
                                Curve = IntensityCurve.Logarithmic
                            },
                            // 2. Magnetic clamps releasing (mechanical release)
                            new PatternLayer
                            {
                                Waveform = WaveformType.Square,
                                Frequency = 40,
                                Amplitude = 0.4f,
                                StartTime = 300,
                                Duration = 140,
                                FadeIn = 10,
                                FadeOut = 25,
                                Curve = IntensityCurve.Linear
                            },
                            // 3. Thrusters spooling up (engine preparation)
                            new PatternLayer
                            {
                                Waveform = WaveformType.Triangle,
                                Frequency = 30,
                                Amplitude = 0.45f,
                                StartTime = 600,
                                Duration = 800,
                                FadeIn = 100,
                                FadeOut = 150,
                                Curve = IntensityCurve.Exponential
                            },
                            // 4. Liftoff thrust (gradual engine power increase)
                            new PatternLayer
                            {
                                Waveform = WaveformType.Sine,
                                Frequency = 32,
                                Amplitude = 0.55f,
                                StartTime = 1200,
                                Duration = 1000,
                                FadeIn = 200,
                                FadeOut = 400,
                                Curve = IntensityCurve.Exponential
                            }
                        }
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
                ["ShieldsDown"] = new EventMapping
                {
                    EventType = "ShieldsDown",
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
                
                ["SupercruiseEntry"] = new EventMapping
                {
                    EventType = "SupercruiseEntry",
                    Pattern = new HapticPattern
                    {
                        Name = "Supercruise Entry",
                        Pattern = PatternType.BuildupRumble,
                        Frequency = 30,
                        Duration = 1500,
                        Intensity = 50,
                        FadeIn = 300,
                        FadeOut = 500
                    },
                    Enabled = true
                },
                
                ["SupercruiseExit"] = new EventMapping
                {
                    EventType = "SupercruiseExit",
                    Pattern = new HapticPattern
                    {
                        Name = "Supercruise Exit",
                        Pattern = PatternType.Impact,
                        Frequency = 40,
                        Duration = 800,
                        Intensity = 60,
                        FadeIn = 100,
                        FadeOut = 300
                    },
                    Enabled = true
                },

                // Additional event name variations based on actual Elite Dangerous journal events
                ["ShieldState"] = new EventMapping 
                {
                    EventType = "ShieldState",
                    Pattern = new HapticPattern
                    {
                        Name = "Shield State Change",
                        Pattern = PatternType.SharpPulse,
                        Frequency = 45,
                        Duration = 500,
                        Intensity = 70,
                        FadeIn = 100,
                        FadeOut = 200
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