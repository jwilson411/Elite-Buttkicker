# Elite Dangerous Buttkicker Extension

[![License: MIT](https://img.shields.io/github/license/jwilson411/Elite-Buttkicker)](LICENSE)

A C# application that monitors Elite Dangerous journal files and generates bass audio signals for buttkicker haptic feedback.

## Features

- **Real-time Journal Monitoring**: Watches Elite Dangerous journal files for game events
- **Audio Device Selection**: Choose the output audio device in the local web interface
- **Configurable Patterns**: JSON-based event mapping with customizable haptic patterns
- **Multiple Event Support**: 51 wired events — FSD jumps, docking, hull damage, on-foot Odyssey warnings, and more
- **Bass-Optimized Audio**: 20-80Hz sine waves optimized for buttkicker hardware

## Quick Start

### Download a ready-to-run Windows build

1. Open the [latest GitHub Release](https://github.com/jwilson411/Elite-Buttkicker/releases/latest).
2. Download the ZIP for your Windows architecture. Most users should choose `win-x64`; Windows on ARM users should choose `win-arm64`.
3. Extract the entire ZIP to a writable folder.
4. Run `EDButtkicker.exe`, then use the automatically opened browser interface to select your audio device and verify the journal path.

Each ZIP has a matching `.sha256` file. You can verify it in PowerShell with:

```powershell
(Get-FileHash .\Elite-Buttkicker-vX.Y.Z-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
```

The value must match the first value in the downloaded `.sha256` file. Windows may warn about the unsigned executable; verify the checksum and that the download came from this repository before choosing to run it.

### Build from source

1. **Build the project**:
   ```bash
   dotnet build
   ```

2. **Run the application**:
   ```bash
   dotnet run --project src/EDButtkicker
   ```

3. **Finish setup in the web interface**:

   The application does not ask anything in the console. It starts straight away with the system
   default audio device and an auto-detected journal folder, serves its interface on
   <http://localhost:47811> (loopback only) and opens that address in your browser. If the browser
   does not open, or you started the app over a remote session, open the address yourself on the
   same machine.

   In that interface:
   - Select your audio output device (buttkicker/subwoofer)
   - Confirm the Elite Dangerous journal path
   - Start Elite Dangerous and play!

## Configuration

### Audio Settings (`config/appsettings.json`)
- **SampleRate**: 44100Hz (recommended for compatibility)
- **DefaultFrequency**: 40Hz (optimal for buttkickers)
- **MaxIntensity**: 80% (safe maximum level)

### Event Patterns (`patterns/default-patterns.json`)
- **FSDJump**: 3-second buildup rumble (35Hz, 90% intensity)
- **Docked**: Sharp impact with fade (45Hz, 70% intensity)  
- **HullDamage**: Damage-scaled pulses (50Hz, variable intensity)
- **ShipTargeted**: Brief target lock pulse (60Hz, 40% intensity)
- **FighterDestroyed**: Intense explosion burst (30Hz, 95% intensity)

## Supported Events

51 event types are wired in the default configuration. Events marked ⚙️ are disabled by
default — enable them in the pattern editor or in `patterns/default-patterns.json`.

### Hyperspace & Supercruise
| Event | Default | Pattern | Description |
|-------|---------|---------|-------------|
| `StartJump` | ✅ | MultiLayer buildup | Hyperspace jump initiation — 5 s exponential buildup (35 Hz, 90%) |
| `FSDJump` | ✅ | Sequence | Hyperspace arrival — thump + settling rumble (38 Hz, 70%) |
| `FsdJumpInProgress` | ✅ | SharpPulse | Hyperspace transit mid-jump pulse (38 Hz, 50%) |
| `FsdCooldown` | ✅ | Oscillating | FSD cooldown oscillation after jump (30 Hz, 35%) |
| `SupercruiseEntry` | ✅ | BuildupRumble | Supercruise engage buildup (30 Hz, 50%) |
| `SupercruiseExit` | ✅ | Impact | Supercruise drop-out impact (40 Hz, 60%) |
| `SupercruiseDestinationDrop` | ✅ | Impact | Destination proximity drop (38 Hz, 50%) |
| `JetConeBoost` | ✅ | Oscillating | Neutron star cone boost (25 Hz, 80%, 3 s) |

### Station & Docking
| Event | Default | Pattern | Description |
|-------|---------|---------|-------------|
| `Docked` | ✅ | Sequence | Full docking sequence — contact, clamps, fuel hose (38 Hz, 5.5 s) |
| `Undocked` | ✅ | Sequence | Undocking sequence — fuel disconnect, clamp release, liftoff (38 Hz, 2 s) |
| `DockingGranted` | ✅ | SharpPulse | Docking clearance confirmation pulse (55 Hz, 120 ms) |
| `RefuelAll` | ✅ | SustainedRumble | Station refuel sustained rumble (32 Hz, 1.2 s) |

### Combat
| Event | Default | Pattern | Description |
|-------|---------|---------|-------------|
| `HullDamage` | ✅ | SharpPulse | Hull hit — intensity scales with damage amount (50 Hz) |
| `CriticalDamageSequence` | ✅ | Sequence | Chained critical damage alert — triggers below 25% hull (60 Hz, 100%) |
| `ShieldDown` | ✅ | Impact | Shield collapse — heavy impact (35 Hz, 90%, 1 s) |
| `ShieldsUp` | ✅ | BuildupRumble | Shields back online (50 Hz, 60%) |
| `ShieldState` | ✅ | SharpPulse | Generic shield state change pulse (45 Hz, 60%) |
| `FighterDestroyed` | ✅ | Impact | Explosion burst — fighter lost (30 Hz, 95%, 1 s) |
| `UnderAttack` | ⚙️ | SharpPulse | Active attack pulses (70 Hz, 95%) — disabled by default to avoid spam |
| `ShipTargeted` | ⚙️ | SharpPulse | Target lock confirmation (60 Hz, 40%, 150 ms) |
| `Interdicted` | ✅ | Oscillating | Being pulled from supercruise (45 Hz, 75%, 4 s) |
| `Interdiction` | ✅ | BuildupRumble | Interdicting another ship (40 Hz, 75%, 3.5 s) |

### Planetary Operations
| Event | Default | Pattern | Description |
|-------|---------|---------|-------------|
| `Touchdown` | ✅ | Sequence | Planetary landing — gear contact, settle, clamps (25 Hz, 5 s) |
| `Liftoff` | ✅ | Sequence | Planetary liftoff — clamp release, thruster spool, climb (30 Hz, 3 s) |
| `GlideModeOn` | ✅ | SharpPulse | Atmospheric glide engaged (42 Hz, 45%) |
| `GlideModeOff` | ✅ | SharpPulse | Atmospheric glide ended (38 Hz, 35%) |

### Heat & Warnings
| Event | Default | Pattern | Description |
|-------|---------|---------|-------------|
| `HeatWarning` | ✅ | Oscillating | Ship overheating warning (55 Hz, 60%, 1.5 s) |
| `HeatDamage` | ✅ | Oscillating | Active heat damage (65 Hz, 85%, 0.8 s) |
| `Overheating` | ✅ | Oscillating | Critical overheating alert (60 Hz, 80%, 1.5 s) |
| `LowFuel` | ✅ | Oscillating | Low fuel warning (45 Hz, 70%, 2 s) |

### Ship Systems
| Event | Default | Pattern | Description |
|-------|---------|---------|-------------|
| `LandingGearDown` | ✅ | Sequence | Gear deploy — clunk, hydraulic extension, lock (38 Hz, 3 s) |
| `LandingGearUp` | ✅ | Sequence | Gear retract — pins release, hydraulic pull, stow (38 Hz, 2.5 s) |
| `HardpointsDeployed` | ✅ | Sequence | Weapons deploy — mechanical clunk + tone (45 Hz, 95%, 0.8 s) |
| `HardpointsRetracted` | ✅ | SharpPulse | Weapons retract (42 Hz, 40%) |
| `CargoScoopDeployed` | ✅ | BuildupRumble | Cargo scoop extend (35 Hz, 40%) |
| `CargoScoopRetracted` | ✅ | SharpPulse | Cargo scoop retract (38 Hz, 30%) |
| `SilentRunningOn` | ✅ | Fade | Silent running engaged — fades to quiet (28 Hz, 45%) |
| `SilentRunningOff` | ✅ | BuildupRumble | Silent running disengaged (38 Hz, 40%) |
| `NightVisionOn` | ⚙️ | SharpPulse | Night vision toggle on (52 Hz, 25%) |
| `NightVisionOff` | ⚙️ | SharpPulse | Night vision toggle off (48 Hz, 20%) |

### Fighter Bay
| Event | Default | Pattern | Description |
|-------|---------|---------|-------------|
| `LaunchFighter` | ✅ | BuildupRumble | Fighter launch from bay (40 Hz, 60%, 1.5 s) |
| `DockFighter` | ✅ | Impact | Fighter recovered into bay (45 Hz, 55%, 0.6 s) |

### On Foot (Odyssey)
| Event | Default | Pattern | Description |
|-------|---------|---------|-------------|
| `OnFoot` | ✅ | SharpPulse | Transition to on-foot (suit disembarked) (40 Hz, 40%) |
| `LowOxygen` | ✅ | Oscillating | Low oxygen suit warning (45 Hz, 70%, 2 s) |
| `LowHealth` | ✅ | Oscillating | Low health warning (50 Hz, 75%, 2 s) |
| `Cold` | ✅ | SharpPulse | Cold environment alert (35 Hz, 35%) |
| `VeryCold` | ✅ | Oscillating | Extreme cold warning (35 Hz, 70%, 1.5 s) |
| `Hot` | ✅ | SharpPulse | Hot environment alert (55 Hz, 35%) |
| `VeryHot` | ✅ | Oscillating | Extreme heat warning (60 Hz, 80%, 1.5 s) |

### Trading & Colonisation
| Event | Default | Pattern | Description |
|-------|---------|---------|-------------|
| `FuelScoop` | ⚙️ | SustainedRumble | Star fuel scooping rumble (35 Hz, 50%, 2.5 s) |
| `ColonisationContribution` | ⚙️ | Sequence | Cargo delivered to colony (40 Hz, 65%, 1.8 s) |
| `MarketBuy` | ⚙️ | SharpPulse | Cargo loaded at market (45 Hz, 25%) |

### Engine Features
- **Dynamic intensity scaling** — `HullDamage` and `CriticalDamageSequence` scale with damage amount; heat events scale with temperature
- **Oscillating patterns** — Amplitude-modulated waveforms for sustained warnings (heat, interdiction, low fuel)
- **Rate limiting** — Prevents audio saturation while maintaining responsiveness
- **Context-aware conditions** — `CriticalDamageSequence` triggers only below 25% hull; `HullDamage` voice at 50%

## Technical Details

- **Framework**: .NET 8.0
- **Audio**: NAudio with WASAPI device enumeration
- **Monitoring**: FileSystemWatcher for real-time journal parsing
- **Architecture**: Hosted services with dependency injection
- **Logging**: Structured logging with configurable levels

## Requirements

- .NET 8.0 Runtime
- Windows (for audio device access)
- Elite Dangerous (for journal files)
- Audio output device (buttkicker, subwoofer, etc.)

## Safety Notes

- Audio levels are capped at configured maximum
- The cap is applied after mixing for all pattern types and honors `Audio.MaxIntensity`
- Smooth sine waves prevent speaker damage
- Rate limiting prevents audio spam
- Graceful error handling for device disconnection

## Development

The project follows the specifications in `claude.md` and implements:
- Clean dependency injection architecture
- Configurable event mappings
- Real-time audio generation
- Robust error handling
- Extensible pattern system

For advanced configuration, modify the JSON files in `config/` and `patterns/` directories.

## Building and Publishing

### Development Build
For local development and testing:
```bash
dotnet build
dotnet run --project src/EDButtkicker
```

### Release Build
For optimized performance:
```bash
dotnet build -c Release
dotnet run -c Release --project src/EDButtkicker
```

### Publishing for Distribution

#### Self-Contained Executable (Recommended)
Creates a single-file executable with all dependencies included:
```bash
# Windows x64 (most common) - Single file without trimming (recommended)
dotnet publish src/EDButtkicker -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64

# Windows x86 (32-bit systems)
dotnet publish src/EDButtkicker -c Release -r win-x86 --self-contained -p:PublishSingleFile=true -o publish/win-x86

# Windows ARM64 (newer ARM-based Windows devices)
dotnet publish src/EDButtkicker -c Release -r win-arm64 --self-contained -p:PublishSingleFile=true -o publish/win-arm64
```

#### Framework-Dependent Build
Smaller file size, requires .NET 8.0 Runtime to be installed:
```bash
dotnet publish src/EDButtkicker -c Release -o publish/framework-dependent
```

### Distribution Files
After publishing, your `publish/` folder will contain:
- `EDButtkicker.exe` - Main executable
- `appsettings.json` - Configuration file
- `wwwroot/` - Web interface files (pattern editor, etc.)
- Additional runtime files (if self-contained)

### Sharing with Friends

1. **Build the self-contained version** (recommended):
   ```bash
   dotnet publish src/EDButtkicker -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -o publish/elite-buttkicker-release
   ```

2. **Create a distributable package**:
   - Copy the entire `publish/elite-buttkicker-release/` folder
   - Rename it to something like `Elite-Buttkicker-v1.0`
   - Zip the folder for easy sharing

3. **Include instructions for your friends**:
   ```
   Elite Dangerous Buttkicker Setup:
   1. Extract the zip file to any folder
   2. Run EDButtkicker.exe
   3. Follow the setup wizard to select your audio device
   4. Start Elite Dangerous and enjoy haptic feedback!

   Note: Windows may show a security warning for unsigned executables.
   Click "More info" then "Run anyway" to continue.
   ```

### Build Script (Optional)
Create a `build.cmd` file for easy building:
```cmd
@echo off
echo Building Elite Dangerous Buttkicker...
dotnet publish src/EDButtkicker -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -o publish/elite-buttkicker-release
echo Build complete! Files are in publish/elite-buttkicker-release/
pause
```

### Troubleshooting Build Issues

**"dotnet command not found"**:
- Install .NET 8.0 SDK from https://dotnet.microsoft.com/download

**Build errors**:
- Ensure you're in the project root directory
- Run `dotnet clean` then `dotnet restore` before building

**Large file sizes**:
- Use `PublishTrimmed=true` to reduce size
- Consider framework-dependent builds if .NET runtime is acceptable

### Build warnings are errors

Every project in this repository (`Directory.Build.props`) compiles with `TreatWarningsAsErrors`. A
compiler or analyzer warning fails the local build the same way it fails CI's dedicated
`build-warnings` job — the goal is a build nobody has to keep quiet about.

If you hit a warning you believe is a false positive (not just inconvenient), request a documented
exception instead of silencing it ad hoc:

1. Confirm the warning is a genuine false positive for your specific case, not just noisy.
2. Add the analyzer ID to `<WarningsNotAsErrors>` in `Directory.Build.props`, on its own entry.
3. Add a one-line comment next to the entry explaining why (link the GitHub issue/PR if there's
   more context).
4. Prefer `WarningsNotAsErrors` (keeps the diagnostic visible in build output) over `NoWarn`
   (hides it entirely). Only use `NoWarn` when the diagnostic is verified to be always wrong for
   this codebase.
5. Call out the exception in your PR description so a reviewer can sanity-check it.

**Antivirus warnings**:
- Self-built executables may trigger false positives
- Add build folder to antivirus exclusions during development