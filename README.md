# Elite Dangerous Buttkicker Extension

A C# application that monitors Elite Dangerous journal files and generates bass audio signals for buttkicker haptic feedback.

## Features

- **Real-time Journal Monitoring**: Watches Elite Dangerous journal files for game events
- **Audio Device Selection**: Interactive console UI for choosing output audio device
- **Configurable Patterns**: JSON-based event mapping with customizable haptic patterns
- **Multiple Event Support**: FSDJump, Docking, Hull Damage, Targeting, and Explosions
- **Bass-Optimized Audio**: 20-80Hz sine waves optimized for buttkicker hardware

## Quick Start

1. **Build the project**:
   ```bash
   dotnet build
   ```

2. **Run the application**:
   ```bash
   dotnet run --project src/EDButtkicker
   ```

3. **Follow the setup prompts**:
   - Select your audio output device (buttkicker/subwoofer)
   - Confirm Elite Dangerous journal path
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

### Core Events (✅ Implemented)
- ✅ **FSDJump** - Hyperspace jump with buildup rumble (35Hz, 3s, 90% intensity)
- ✅ **Docked/Undocked** - Station docking impact and fade (45Hz/40Hz)
- ✅ **HullDamage** - Damage-scaled sharp pulses (50Hz, variable intensity)  
- ✅ **ShipTargeted** - Target lock brief pulse (60Hz, 150ms)

### Combat Events (✅ Implemented)  
- ✅ **UnderAttack** - Intense combat pulses (70Hz, 300ms, 95% intensity)
- ✅ **ShieldDown/ShieldsUp** - Shield status changes with impact/buildup patterns
- ✅ **FighterDestroyed** - Explosion intense burst (30Hz, 1s)

### Planetary Operations (✅ Implemented)
- ✅ **Touchdown/Liftoff** - Planetary landings with ship-mass scaling (25Hz/30Hz)

### Heat & Fuel Management (✅ Implemented)
- ✅ **HeatWarning/HeatDamage** - Oscillating patterns with heat-level scaling (55Hz/65Hz)
- ✅ **FuelScoop** - Star scooping sustained rumble with rate-based intensity (35Hz, 2.5s)

### Fighter Operations (✅ Implemented)
- ✅ **LaunchFighter/DockFighter** - Fighter bay operations (40Hz/45Hz)

### Navigation (✅ Implemented)
- ✅ **JetConeBoost** - Neutron star boost with unique deep oscillation (25Hz, 3s)
- ✅ **Interdicted/Interdiction** - Interdiction events with stress patterns (45Hz/40Hz)

### Advanced Features
- ✅ **Dynamic intensity scaling** - Events adjust based on ship type, damage levels, heat, etc.
- ✅ **Oscillating patterns** - Amplitude modulation for heat warnings, interdictions, neutron boosts
- ✅ **Rate limiting** - Prevents audio spam while maintaining responsiveness
- ✅ **Context-aware modifications** - Ship mass, jump distance, heat levels affect feedback

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