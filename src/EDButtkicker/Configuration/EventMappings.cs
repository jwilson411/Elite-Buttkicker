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
						Duration = 5000,
						Intensity = 90,
						FadeIn = 500,
						FadeOut = 1000,
						MaxIntensity = 100,
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

				["FSDJump"] = new EventMapping
				{
					EventType = "FSDJump",
					Pattern = new HapticPattern
					{
						Name = "Hyperspace Arrival",
						Pattern = PatternType.Sequence,
						Frequency = 38,
						Duration = 2000,
						Intensity = 70,
						FadeIn = 0,
						FadeOut = 500,
						MaxIntensity = 100,
						Layers = new List<PatternLayer>
						{
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 35, Amplitude = 0.8f,  StartTime = 0,   Duration = 150, FadeIn = 0,   FadeOut = 40,  Curve = IntensityCurve.Linear },
							new PatternLayer { Waveform = WaveformType.Triangle, Frequency = 30, Amplitude = 0.5f,  StartTime = 200, Duration = 800, FadeIn = 50,  FadeOut = 300, Curve = IntensityCurve.Logarithmic },
							new PatternLayer { Waveform = WaveformType.Sine,     Frequency = 42, Amplitude = 0.35f, StartTime = 800, Duration = 900, FadeIn = 100, FadeOut = 500, Curve = IntensityCurve.Exponential }
						}
					},
					Enabled = true
				},

				["Docked"] = new EventMapping
				{
					EventType = "Docked",
					Pattern = new HapticPattern
					{
						Name = "Station Docking Sequence",
						Pattern = PatternType.Sequence,
						Frequency = 38,
						Duration = 5500,
						Intensity = 100,
						FadeIn = 0,
						FadeOut = 600,
						MaxIntensity = 100,
						Layers = new List<PatternLayer>
						{
							// 1. Initial pad contact - ship weight hitting the landing pad
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 28, Amplitude = 0.95f, StartTime = 0,    Duration = 250,  FadeIn = 0,   FadeOut = 60,  Curve = IntensityCurve.Linear },
							// 2. Ship settling under its own weight - low heavy rumble
							new PatternLayer { Waveform = WaveformType.Triangle, Frequency = 22, Amplitude = 0.7f,  StartTime = 200,  Duration = 600,  FadeIn = 80,  FadeOut = 200, Curve = IntensityCurve.Logarithmic },
							// 3. Secondary bounce/settle - ship rocking to rest
							new PatternLayer { Waveform = WaveformType.Sine,     Frequency = 25, Amplitude = 0.45f, StartTime = 700,  Duration = 400,  FadeIn = 100, FadeOut = 250, Curve = IntensityCurve.Logarithmic },
							// 4. Magnetic clamps engaging - sharp mechanical lock
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 42, Amplitude = 0.85f, StartTime = 1300, Duration = 180,  FadeIn = 0,   FadeOut = 40,  Curve = IntensityCurve.Linear },
							// 5. Clamp pressure building - hydraulic hold
							new PatternLayer { Waveform = WaveformType.Triangle, Frequency = 35, Amplitude = 0.5f,  StartTime = 1500, Duration = 500,  FadeIn = 50,  FadeOut = 200, Curve = IntensityCurve.Exponential },
							// 6. Second clamp set locking (larger ships have multiple)
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 40, Amplitude = 0.7f,  StartTime = 2100, Duration = 150,  FadeIn = 0,   FadeOut = 30,  Curve = IntensityCurve.Linear },
							// 7. Fuel hose connecting - brief mechanical thud
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 48, Amplitude = 0.55f, StartTime = 2800, Duration = 200,  FadeIn = 10,  FadeOut = 50,  Curve = IntensityCurve.Linear },
							// 8. Fuel flowing - sustained low rumble, fading as tank fills
							new PatternLayer { Waveform = WaveformType.Sine,     Frequency = 32, Amplitude = 0.5f,  StartTime = 3100, Duration = 2000, FadeIn = 200, FadeOut = 900, Curve = IntensityCurve.Exponential }
						}
					},
					Enabled = true
				},

				["Undocked"] = new EventMapping
				{
					EventType = "Undocked",
					Pattern = new HapticPattern
					{
						Name = "Station Undocking Sequence",
						Pattern = PatternType.Sequence,
						Frequency = 38,
						Duration = 2000,
						Intensity = 75,
						FadeIn = 0,
						FadeOut = 400,
						MaxIntensity = 100,
						Layers = new List<PatternLayer>
						{
							// 1. Fuel line disconnect - quick sharp pull
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 46, Amplitude = 0.6f,  StartTime = 0,   Duration = 120,  FadeIn = 0,   FadeOut = 30,  Curve = IntensityCurve.Linear },
							// 2. Clamps releasing - lighter than engage, just a snap
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 40, Amplitude = 0.65f, StartTime = 300, Duration = 140,  FadeIn = 0,   FadeOut = 25,  Curve = IntensityCurve.Linear },
							// 3. Ship lifting off pad - brief thruster vibration
							new PatternLayer { Waveform = WaveformType.Triangle, Frequency = 30, Amplitude = 0.45f, StartTime = 550, Duration = 800,  FadeIn = 80,  FadeOut = 400, Curve = IntensityCurve.Exponential },
							// 4. Clear of pad - fades as ship moves into mail slot
							new PatternLayer { Waveform = WaveformType.Sine,     Frequency = 26, Amplitude = 0.3f,  StartTime = 1200, Duration = 600, FadeIn = 100, FadeOut = 400, Curve = IntensityCurve.Exponential }
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
							["health_below"] = 0.5
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
						Intensity = 40,
						MaxIntensity = 100
					},
					Enabled = false
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
						FadeOut = 600,
						MaxIntensity = 100
					},
					Enabled = true
				},

				["Touchdown"] = new EventMapping
				{
					EventType = "Touchdown",
					Pattern = new HapticPattern
					{
						Name = "Planetary Touchdown Sequence",
						Pattern = PatternType.Sequence,
						Frequency = 30,
						Duration = 5000,
						Intensity = 100,
						FadeIn = 0,
						FadeOut = 600,
						MaxIntensity = 100,
						Layers = new List<PatternLayer>
						{
							// 1. Gear making first ground contact - hard impact
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 25, Amplitude = 1.0f,  StartTime = 0,    Duration = 300,  FadeIn = 0,   FadeOut = 80,  Curve = IntensityCurve.Linear },
							// 2. Ship mass transferring to ground - heavy deep rumble
							new PatternLayer { Waveform = WaveformType.Triangle, Frequency = 20, Amplitude = 0.8f,  StartTime = 200,  Duration = 800,  FadeIn = 60,  FadeOut = 300, Curve = IntensityCurve.Logarithmic },
							// 3. Ground surface vibration - planet surface texture
							new PatternLayer { Waveform = WaveformType.Sine,     Frequency = 35, Amplitude = 0.5f,  StartTime = 400,  Duration = 600,  FadeIn = 100, FadeOut = 300, Curve = IntensityCurve.Logarithmic },
							// 4. Secondary bounce - ship rocking on uneven ground
							new PatternLayer { Waveform = WaveformType.Triangle, Frequency = 22, Amplitude = 0.55f, StartTime = 900,  Duration = 500,  FadeIn = 80,  FadeOut = 250, Curve = IntensityCurve.Logarithmic },
							// 5. Magnetic clamps engaging - sharp lock into ground anchors
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 40, Amplitude = 0.85f, StartTime = 1600, Duration = 200,  FadeIn = 0,   FadeOut = 40,  Curve = IntensityCurve.Linear },
							// 6. Second clamp set locking
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 38, Amplitude = 0.75f, StartTime = 1900, Duration = 160,  FadeIn = 0,   FadeOut = 35,  Curve = IntensityCurve.Linear },
							// 7. Clamp hydraulic pressure building
							new PatternLayer { Waveform = WaveformType.Triangle, Frequency = 30, Amplitude = 0.45f, StartTime = 2100, Duration = 700,  FadeIn = 80,  FadeOut = 300, Curve = IntensityCurve.Exponential },
							// 8. Systems connecting - pad services linking up
							new PatternLayer { Waveform = WaveformType.Sine,     Frequency = 42, Amplitude = 0.35f, StartTime = 3000, Duration = 1600, FadeIn = 200, FadeOut = 800, Curve = IntensityCurve.Exponential }
						}
					},
					Enabled = true
				},

				["Liftoff"] = new EventMapping
				{
					EventType = "Liftoff",
					Pattern = new HapticPattern
					{
						Name = "Planetary Liftoff Sequence",
						Pattern = PatternType.Sequence,
						Frequency = 30,
						Duration = 3000,
						Intensity = 85,
						FadeIn = 0,
						FadeOut = 600,
						MaxIntensity = 100,
						Layers = new List<PatternLayer>
						{
							// 1. Clamps releasing - lighter snap than engage
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 38, Amplitude = 0.65f, StartTime = 0,    Duration = 140,  FadeIn = 0,   FadeOut = 30,  Curve = IntensityCurve.Linear },
							// 2. Second clamp set releasing
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 36, Amplitude = 0.55f, StartTime = 200,  Duration = 120,  FadeIn = 0,   FadeOut = 25,  Curve = IntensityCurve.Linear },
							// 3. Thrusters spooling up - building vibration
							new PatternLayer { Waveform = WaveformType.Triangle, Frequency = 28, Amplitude = 0.6f,  StartTime = 450,  Duration = 900,  FadeIn = 150, FadeOut = 100, Curve = IntensityCurve.Exponential },
							// 4. Gear leaving ground - brief separation thud
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 24, Amplitude = 0.7f,  StartTime = 1100, Duration = 200,  FadeIn = 0,   FadeOut = 60,  Curve = IntensityCurve.Linear },
							// 5. Climbing thrust - strong engine rumble
							new PatternLayer { Waveform = WaveformType.Sine,     Frequency = 32, Amplitude = 0.55f, StartTime = 1300, Duration = 1200, FadeIn = 100, FadeOut = 500, Curve = IntensityCurve.Exponential },
							// 6. Fading as ship clears the surface
							new PatternLayer { Waveform = WaveformType.Sine,     Frequency = 26, Amplitude = 0.3f,  StartTime = 2000, Duration = 800,  FadeIn = 200, FadeOut = 600, Curve = IntensityCurve.Exponential }
						}
					},
					Enabled = true
				},

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
						FadeOut = 200,
						MaxIntensity = 100
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
						FadeOut = 200,
						MaxIntensity = 100
					},
					Enabled = true
				},

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
						FadeOut = 600,
						MaxIntensity = 100
					},
					Enabled = false
				},

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
						FadeOut = 100,
						MaxIntensity = 100
					},
					Enabled = false
				},

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
						FadeOut = 400,
						MaxIntensity = 100
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
						FadeOut = 200,
						MaxIntensity = 100
					},
					Enabled = true
				},

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
						FadeOut = 800,
						MaxIntensity = 100
					},
					Enabled = true
				},

				["Interdicted"] = new EventMapping
				{
					EventType = "Interdicted",
					Pattern = new HapticPattern
					{
						Name = "Being Interdicted",
						Pattern = PatternType.Oscillating,
						Frequency = 45,
						Duration = 4000,
						Intensity = 75,
						FadeIn = 300,
						FadeOut = 500,
						MaxIntensity = 100
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
						FadeOut = 600,
						MaxIntensity = 100
					},
					Enabled = true
				},

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
						FadeOut = 400,
						MaxIntensity = 100
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
						MaxIntensity = 100,
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
						FadeOut = 500,
						MaxIntensity = 100
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
						FadeOut = 300,
						MaxIntensity = 100
					},
					Enabled = true
				},

				["ShieldState"] = new EventMapping
				{
					EventType = "ShieldState",
					Pattern = new HapticPattern
					{
						Name = "Shield State Change",
						Pattern = PatternType.SharpPulse,
						Frequency = 45,
						Duration = 500,
						Intensity = 60,
						FadeIn = 100,
						FadeOut = 200,
						MaxIntensity = 100
					},
					Enabled = true
				},

				// --- Landing Gear (enhanced) ---
				["LandingGearDown"] = new EventMapping
				{
					EventType = "LandingGearDown",
					Pattern = new HapticPattern
					{
						Name = "Landing Gear Deploy",
						Pattern = PatternType.Sequence,
						Frequency = 38,
						Duration = 3000,
						Intensity = 90,
						FadeIn = 0,
						FadeOut = 300,
						MaxIntensity = 100,
						Layers = new List<PatternLayer>
						{
							// Initial deploy clunk - hard mechanical release
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 42, Amplitude = 0.9f,  StartTime = 0,    Duration = 180,  FadeIn = 0,   FadeOut = 40,  Curve = IntensityCurve.Linear },
							// First stage extension - heavy hydraulic rumble
							new PatternLayer { Waveform = WaveformType.Triangle, Frequency = 30, Amplitude = 0.65f, StartTime = 200,  Duration = 600,  FadeIn = 60,  FadeOut = 100, Curve = IntensityCurve.Logarithmic },
							// Mid-travel vibration - gear moving through airframe
							new PatternLayer { Waveform = WaveformType.Sine,     Frequency = 48, Amplitude = 0.45f, StartTime = 700,  Duration = 700,  FadeIn = 100, FadeOut = 200, Curve = IntensityCurve.Linear },
							// Second clunk - gear reaching full extension
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 36, Amplitude = 0.8f,  StartTime = 1500, Duration = 200,  FadeIn = 0,   FadeOut = 50,  Curve = IntensityCurve.Linear },
							// Locking pins engaging - final mechanical confirmation
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 44, Amplitude = 0.7f,  StartTime = 1800, Duration = 120,  FadeIn = 0,   FadeOut = 30,  Curve = IntensityCurve.Linear },
							// Hydraulic pressure settling - system stabilising
							new PatternLayer { Waveform = WaveformType.Sine,     Frequency = 28, Amplitude = 0.35f, StartTime = 2000, Duration = 800,  FadeIn = 100, FadeOut = 400, Curve = IntensityCurve.Exponential }
						}
					},
					Enabled = true
				},

				["LandingGearUp"] = new EventMapping
				{
					EventType = "LandingGearUp",
					Pattern = new HapticPattern
					{
						Name = "Landing Gear Retract",
						Pattern = PatternType.Sequence,
						Frequency = 38,
						Duration = 2500,
						Intensity = 90,
						FadeIn = 0,
						FadeOut = 300,
						MaxIntensity = 100,
						Layers = new List<PatternLayer>
						{
							// Locking pins releasing
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 44, Amplitude = 0.75f, StartTime = 0,    Duration = 120,  FadeIn = 0,   FadeOut = 25,  Curve = IntensityCurve.Linear },
							// Unlock clunk
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 38, Amplitude = 0.7f,  StartTime = 150,  Duration = 150,  FadeIn = 0,   FadeOut = 30,  Curve = IntensityCurve.Linear },
							// Hydraulic retraction - strong pull
							new PatternLayer { Waveform = WaveformType.Triangle, Frequency = 32, Amplitude = 0.6f,  StartTime = 350,  Duration = 700,  FadeIn = 40,  FadeOut = 150, Curve = IntensityCurve.Logarithmic },
							// Mid-travel through airframe
							new PatternLayer { Waveform = WaveformType.Sine,     Frequency = 46, Amplitude = 0.4f,  StartTime = 900,  Duration = 600,  FadeIn = 80,  FadeOut = 200, Curve = IntensityCurve.Linear },
							// Final stow thud - gear locked away
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 40, Amplitude = 0.65f, StartTime = 1600, Duration = 160,  FadeIn = 0,   FadeOut = 40,  Curve = IntensityCurve.Linear },
							// Bay doors closing - gentle fade
							new PatternLayer { Waveform = WaveformType.Sine,     Frequency = 26, Amplitude = 0.3f,  StartTime = 1850, Duration = 500,  FadeIn = 80,  FadeOut = 350, Curve = IntensityCurve.Exponential }
						}
					},
					Enabled = true
				},

				// --- Hardpoints ---
				["HardpointsDeployed"] = new EventMapping
				{
					EventType = "HardpointsDeployed",
					Pattern = new HapticPattern
					{
						Name = "Hardpoints Deploy",
						Pattern = PatternType.Sequence,
						Frequency = 45,
						Duration = 800,
						Intensity = 95,
						FadeIn = 0,
						FadeOut = 150,
						MaxIntensity = 100,
						Layers = new List<PatternLayer>
						{
							new PatternLayer { Waveform = WaveformType.Square, Frequency = 48, Amplitude = 0.75f, StartTime = 0,   Duration = 100, FadeIn = 0,  FadeOut = 20,  Curve = IntensityCurve.Linear },
							new PatternLayer { Waveform = WaveformType.Sine,   Frequency = 55, Amplitude = 0.4f,  StartTime = 150, Duration = 500, FadeIn = 50, FadeOut = 200, Curve = IntensityCurve.Exponential },
							new PatternLayer { Waveform = WaveformType.Square, Frequency = 50, Amplitude = 0.55f, StartTime = 600, Duration = 100, FadeIn = 0,  FadeOut = 30,  Curve = IntensityCurve.Linear }
						}
					},
					Enabled = true
				},

				["HardpointsRetracted"] = new EventMapping
				{
					EventType = "HardpointsRetracted",
					Pattern = new HapticPattern
					{
						Name = "Hardpoints Retract",
						Pattern = PatternType.SharpPulse,
						Frequency = 42,
						Duration = 500,
						Intensity = 40,
						FadeIn = 0,
						FadeOut = 150,
						MaxIntensity = 100
					},
					Enabled = true
				},

				// --- Cargo Scoop ---
				["CargoScoopDeployed"] = new EventMapping
				{
					EventType = "CargoScoopDeployed",
					Pattern = new HapticPattern
					{
						Name = "Cargo Scoop Deploy",
						Pattern = PatternType.BuildupRumble,
						Frequency = 35,
						Duration = 700,
						Intensity = 40,
						FadeIn = 100,
						FadeOut = 250,
						MaxIntensity = 100
					},
					Enabled = true
				},

				["CargoScoopRetracted"] = new EventMapping
				{
					EventType = "CargoScoopRetracted",
					Pattern = new HapticPattern
					{
						Name = "Cargo Scoop Retract",
						Pattern = PatternType.SharpPulse,
						Frequency = 38,
						Duration = 240,
						Intensity = 30,
						FadeIn = 0,
						FadeOut = 100,
						MaxIntensity = 100
					},
					Enabled = true
				},

				// --- Silent Running ---
				["SilentRunningOn"] = new EventMapping
				{
					EventType = "SilentRunningOn",
					Pattern = new HapticPattern
					{
						Name = "Silent Running Engaged",
						Pattern = PatternType.Fade,
						Frequency = 28,
						Duration = 1000,
						Intensity = 45,
						FadeIn = 400,
						FadeOut = 500,
						MaxIntensity = 100
					},
					Enabled = true
				},

				["SilentRunningOff"] = new EventMapping
				{
					EventType = "SilentRunningOff",
					Pattern = new HapticPattern
					{
						Name = "Silent Running Disengaged",
						Pattern = PatternType.BuildupRumble,
						Frequency = 38,
						Duration = 600,
						Intensity = 40,
						FadeIn = 50,
						FadeOut = 200,
						MaxIntensity = 100
					},
					Enabled = true
				},

				// --- FSD States ---
				["FsdCooldown"] = new EventMapping
				{
					EventType = "FsdCooldown",
					Pattern = new HapticPattern
					{
						Name = "FSD Cooldown",
						Pattern = PatternType.Oscillating,
						Frequency = 30,
						Duration = 1500,
						Intensity = 35,
						FadeIn = 200,
						FadeOut = 600,
						MaxIntensity = 100
					},
					Enabled = true
				},

				// --- Warnings ---
				["LowFuel"] = new EventMapping
				{
					EventType = "LowFuel",
					Pattern = new HapticPattern
					{
						Name = "Low Fuel Warning",
						Pattern = PatternType.Oscillating,
						Frequency = 45,
						Duration = 2000,
						Intensity = 70,
						FadeIn = 100,
						FadeOut = 400,
						MaxIntensity = 100
					},
					Enabled = true
				},

				["Overheating"] = new EventMapping
				{
					EventType = "Overheating",
					Pattern = new HapticPattern
					{
						Name = "Ship Overheating",
						Pattern = PatternType.Oscillating,
						Frequency = 60,
						Duration = 1500,
						Intensity = 80,
						FadeIn = 50,
						FadeOut = 300,
						MaxIntensity = 100
					},
					Enabled = true
				},

				// --- Night Vision ---
				["NightVisionOn"] = new EventMapping
				{
					EventType = "NightVisionOn",
					Pattern = new HapticPattern
					{
						Name = "Night Vision On",
						Pattern = PatternType.SharpPulse,
						Frequency = 52,
						Duration = 100,
						Intensity = 25,
						FadeIn = 0,
						FadeOut = 50,
						MaxIntensity = 100
					},
					Enabled = false
				},

				["NightVisionOff"] = new EventMapping
				{
					EventType = "NightVisionOff",
					Pattern = new HapticPattern
					{
						Name = "Night Vision Off",
						Pattern = PatternType.SharpPulse,
						Frequency = 48,
						Duration = 100,
						Intensity = 20,
						FadeIn = 0,
						FadeOut = 50,
						MaxIntensity = 100
					},
					Enabled = false
				},

				// --- Colonisation ---
				["DockingGranted"] = new EventMapping
				{
					EventType = "DockingGranted",
					Pattern = new HapticPattern
					{
						Name = "Docking Clearance",
						Pattern = PatternType.SharpPulse,
						Frequency = 55,
						Duration = 120,
						Intensity = 35,
						FadeIn = 5,
						FadeOut = 40,
						MaxIntensity = 100
					},
					Enabled = true
				},

				["SupercruiseDestinationDrop"] = new EventMapping
				{
					EventType = "SupercruiseDestinationDrop",
					Pattern = new HapticPattern
					{
						Name = "Destination Drop",
						Pattern = PatternType.Impact,
						Frequency = 38,
						Duration = 600,
						Intensity = 50,
						FadeIn = 20,
						FadeOut = 300,
						MaxIntensity = 100
					},
					Enabled = true
				},

				["ColonisationContribution"] = new EventMapping
				{
					EventType = "ColonisationContribution",
					Pattern = new HapticPattern
					{
						Name = "Cargo Delivered",
						Pattern = PatternType.Sequence,
						Frequency = 40,
						Duration = 1800,
						Intensity = 65,
						FadeIn = 10,
						FadeOut = 400,
						MaxIntensity = 100,
						Layers = new List<PatternLayer>
						{
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 38, Amplitude = 0.5f,  StartTime = 0,    Duration = 150, FadeIn = 5,   FadeOut = 30,  Curve = IntensityCurve.Linear },
							new PatternLayer { Waveform = WaveformType.Triangle, Frequency = 32, Amplitude = 0.55f, StartTime = 300,  Duration = 900, FadeIn = 100, FadeOut = 200, Curve = IntensityCurve.Logarithmic },
							new PatternLayer { Waveform = WaveformType.Square,   Frequency = 42, Amplitude = 0.6f,  StartTime = 1400, Duration = 200, FadeIn = 10,  FadeOut = 80,  Curve = IntensityCurve.Linear }
						}
					},
					Enabled = false
				},

				["RefuelAll"] = new EventMapping
				{
					EventType = "RefuelAll",
					Pattern = new HapticPattern
					{
						Name = "Refuelling",
						Pattern = PatternType.SustainedRumble,
						Frequency = 32,
						Duration = 1200,
						Intensity = 30,
						FadeIn = 300,
						FadeOut = 500,
						MaxIntensity = 100
					},
					Enabled = true
				},

				["MarketBuy"] = new EventMapping
				{
					EventType = "MarketBuy",
					Pattern = new HapticPattern
					{
						Name = "Cargo Loaded",
						Pattern = PatternType.SharpPulse,
						Frequency = 45,
						Duration = 100,
						Intensity = 25,
						FadeIn = 5,
						FadeOut = 30,
						MaxIntensity = 100
					},
					Enabled = false
				},

				["CriticalDamageSequence"] = new EventMapping
				{
					EventType = "HullDamage",
					Pattern = new HapticPattern
					{
						Name = "Critical Damage Sequence",
						Pattern = PatternType.Sequence,
						Frequency = 60,
						Duration = 500,
						Intensity = 100,
						MaxIntensity = 100,
						ChainedPatterns = new List<string> { "Warning Pulse", "Emergency Alert" },
						Conditions = new Dictionary<string, object>
						{
							["health_below"] = 0.25
						},
						EnableVoiceAnnouncement = true,
						VoiceMessage = "Critical hull damage! Seek immediate repairs!",
						IntensityCurve = IntensityCurve.Exponential
					},
					Enabled = true
				}
			}
		};
	}
}