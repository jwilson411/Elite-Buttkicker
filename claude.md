# Elite Dangerous Buttkicker Extension

## Project Overview
Create a C# application that monitors Elite Dangerous journal files and generates bass audio signals for buttkicker haptic feedback. Route audio through virtual audio devices to provide enhanced tactile feedback for various game events.

## Project Goals
- Monitor Elite Dangerous journal files in real-time
- Parse game events and map them to haptic feedback patterns
- Generate bass tones (20-80Hz) optimized for buttkicker hardware
- Route audio to user-specified audio devices
- Provide configurable intensity and patterns for different events
- Create a clean, distributable Windows application

## Technical Stack
- **Language**: C# (.NET 8.0)
- **Audio Library**: NAudio (for audio generation, device management, routing)
- **JSON Parsing**: System.Text.Json (built-in)
- **File Monitoring**: FileSystemWatcher (built-in)
- **UI Framework**: Console app initially, can evolve to WinForms/WPF later

## Project Structure
```
EDButtkicker/
├── EDButtkicker.sln
├── src/
│   ├── EDButtkicker/
│   │   ├── EDButtkicker.csproj
│   │   ├── Program.cs
│   │   ├── Services/
│   │   │   ├── JournalMonitorService.cs
│   │   │   ├── AudioEngineService.cs
│   │   │   └── EventMappingService.cs
│   │   ├── Models/
│   │   │   ├── JournalEvent.cs
│   │   │   ├── HapticPattern.cs
│   │   │   └── AudioDevice.cs
│   │   └── Configuration/
│   │       ├── AppSettings.cs
│   │       └── EventMappings.cs
├── config/
│   └── appsettings.json
├── patterns/
│   └── default-patterns.json
└── README.md
```

## Key Dependencies (NuGet Packages)
- `NAudio` (latest stable) - Audio processing and device management
- `Microsoft.Extensions.Hosting` - Service hosting and dependency injection
- `Microsoft.Extensions.Configuration` - Configuration management
- `Microsoft.Extensions.Logging` - Logging framework

## Core Features to Implement

### 1. Journal Monitoring
- Monitor `%USERPROFILE%\Saved Games\Frontier Developments\Elite Dangerous\` directory
- Watch for new journal files and real-time updates
- Parse JSON events efficiently without blocking audio processing

### 2. Event Types to Support (Priority Order)
**High Priority:**
- `FSDJump` - Hyperspace jump (long rumble with buildup)
- `Docked` / `Undocked` - Station docking (impact + fade)
- `HullDamage` - Taking damage (sharp pulse, intensity based on damage)
- `ShipTargeted` - Target lock (brief pulse)
- `FighterDestroyed` - Explosions (intense burst)

**Medium Priority:**
- `EngineColour` / `SetUserShipName` - Engine thrust changes
- `TouchDown` / `Liftoff` - Planetary landing/takeoff
- `HeatWarning` - Overheating (oscillating pattern)
- `FuelScoop` - Star scooping (sustained rumble)

**Nice to Have:**
- `Music` - Pulse with background music rhythm
- `Friends` - Social interactions
- `Market` - Trading activities

### 3. Audio Engine Requirements
- Generate clean sine waves at configurable frequencies (default 40Hz)
- Support multiple simultaneous effects (layering)
- Real-time mixing without audio dropouts
- Configurable intensity levels (0-100%)
- Audio device enumeration and selection
- Graceful handling of device disconnection

### 4. Configuration System
- JSON-based configuration files
- Runtime configuration reload
- Event-to-pattern mapping customization
- Audio device selection persistence
- Intensity/frequency adjustment per event type

## Implementation Guidelines

### Audio Generation Best Practices
- Use 44.1kHz sample rate for compatibility
- Generate smooth sine waves to prevent speaker damage
- Implement proper fade-in/fade-out to avoid audio pops
- Use separate audio thread to prevent blocking
- Buffer audio appropriately for smooth playback

### Performance Considerations
- Process journal events asynchronously
- Cache audio patterns to reduce CPU usage
- Use efficient JSON parsing (System.Text.Json)
- Implement proper memory management for audio buffers
- Log performance metrics for optimization

### Error Handling
- Graceful handling of missing journal files
- Audio device disconnection recovery
- Invalid JSON event parsing
- File system permission issues
- Audio buffer underrun/overrun

## Configuration Examples

### appsettings.json
```json
{
  "EliteDangerous": {
    "JournalPath": "%USERPROFILE%\\Saved Games\\Frontier Developments\\Elite Dangerous\\",
    "MonitorLatestOnly": true
  },
  "Audio": {
    "SampleRate": 44100,
    "BufferSize": 1024,
    "DefaultFrequency": 40,
    "MaxIntensity": 80,
    "AudioDeviceName": "Buttkicker"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "EDButtkicker": "Debug"
    }
  }
}
```

### Event Mapping Configuration
```json
{
  "EventMappings": {
    "FSDJump": {
      "Pattern": "BuildupRumble",
      "Frequency": 35,
      "Duration": 3000,
      "Intensity": 90,
      "FadeIn": 500,
      "FadeOut": 1000
    },
    "HullDamage": {
      "Pattern": "SharpPulse", 
      "Frequency": 50,
      "Duration": 200,
      "IntensityFromDamage": true,
      "MaxIntensity": 100
    }
  }
}
```

## Development Phases

### Phase 1: Core Infrastructure
1. Set up project structure with proper dependency injection
2. Implement journal file monitoring with FileSystemWatcher
3. Create basic JSON event parsing
4. Set up logging and configuration systems

### Phase 2: Audio Engine
1. Implement NAudio-based audio generation
2. Create sine wave generator with fade capabilities
3. Implement audio device enumeration and selection
4. Add basic event-to-audio mapping

### Phase 3: Event Processing
1. Add support for high-priority events (FSDJump, Docked, HullDamage)
2. Implement pattern generation (pulses, rumbles, buildups)
3. Add intensity scaling and frequency adjustment
4. Test with actual Elite Dangerous gameplay

### Phase 4: Polish & Distribution
1. Add configuration UI (console-based initially)
2. Implement proper error handling and recovery
3. Add audio device hot-swapping support
4. Create installer/distribution package
5. Write user documentation

## Testing Strategy
- Use sample journal files for automated testing
- Create mock audio devices for CI/CD
- Manual testing with actual buttkicker hardware
- Performance testing with extended gameplay sessions
- Cross-compatibility testing with different audio setups

## Potential Extensions
- Integration with VoiceAttack for voice control
- Web interface for remote configuration
- Integration with other Elite Dangerous tools (EDDI, EDMC)
- Support for other haptic devices
- Visual feedback alongside audio
- Community pattern sharing

## Getting Started
1. Create new C# console application targeting .NET 8.0
2. Add required NuGet packages
3. Implement basic journal file detection
4. Add simple audio device enumeration
5. Create proof-of-concept with single event type (FSDJump)
6. Iterate and expand functionality

Let me know when you're ready to start implementing specific components!