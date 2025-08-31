# Ship-Specific Haptic Patterns Implementation

## Overview
Implemented a comprehensive ship-specific pattern system that automatically switches haptic sequences based on the current ship being flown, addressing the issue where sounds work for one ship but are too long/short for others.

## Key Components Implemented

### 1. ShipTrackingService
**Location**: `src/EDButtkicker/Services/ShipTrackingService.cs`

**Features**:
- **Automatic ship detection** from Elite Dangerous journal events
- **Tracks ship changes** via `LoadGame`, `ShipyardSwap`, `Loadout`, and other events
- **Current ship state management** with ship type, name, ID, and metadata
- **Ship change notifications** for other services to react

**Key Journal Events Monitored**:
- `LoadGame` - Initial ship on game start
- `ShipyardSwap` - When swapping ships at shipyard
- `Loadout` - Ship configuration/loadout changes
- `StoredShips` - Ship inventory updates
- Any event with `Ship`/`ShipID` properties

### 2. Ship-Specific Pattern Models
**Location**: `src/EDButtkicker/Models/ShipSpecificPatterns.cs`

**Core Models**:
- `ShipSpecificPatterns` - Stores patterns for a specific ship
- `ShipPatternLibrary` - Manages all ships and their patterns
- `CurrentShip` - Represents the active ship with full metadata
- `ShipClassifications` - Database of Elite Dangerous ships with size/role data

**Ship Classification System**:
```csharp
// Automatically classifies ships by size and role
Small Ships: Sidewinder, Eagle, Hauler, Viper, etc.
Medium Ships: Asp Explorer, Python, Krait, Federal ships, etc.  
Large Ships: Anaconda, Corvette, Cutter, Type-9, etc.

Roles: Combat, Exploration, Transport, Multipurpose
```

**Pattern Recommendations**:
- **Duration multipliers** based on ship size (Small: 0.7x, Medium: 1.0x, Large: 1.4x)
- **Intensity adjustments** for ship mass (Light ships: 0.8x, Heavy ships: 1.2x)
- **Frequency modifications** (Small ships: +5Hz, Large ships: -5Hz)

### 3. ShipPatternService
**Location**: `src/EDButtkicker/Services/ShipPatternService.cs`

**Core Functionality**:
- **Pattern resolution** - Gets ship-specific pattern or falls back to default
- **File-based persistence** in `%APPDATA%\EDButtkicker\ship-patterns.json`
- **Automatic pattern suggestions** based on ship class
- **Real-time ship switching** with pattern library updates

**Key Methods**:
```csharp
// Get pattern for current ship, fallback to default
HapticPattern? GetPatternForEvent(string eventName)

// Set custom pattern for specific ship
Task SetShipPatternAsync(string shipKey, string eventName, HapticPattern pattern)

// Apply size-based recommended patterns
Task ApplyRecommendedPatternsAsync(string shipKey, string shipType)
```

### 4. Web API Integration
**Location**: `src/EDButtkicker/Controllers/ShipPatternsApiController.cs`

**Available Endpoints**:

#### Ship Management
- `GET /api/ShipPatterns/current-ship` - Get current active ship
- `GET /api/ShipPatterns` - Get all ships with their patterns
- `GET /api/ShipPatterns/{shipKey}` - Get specific ship details
- `DELETE /api/ShipPatterns/{shipKey}` - Remove ship from library

#### Pattern Management  
- `POST /api/ShipPatterns/{shipKey}/patterns` - Set custom pattern for ship
- `DELETE /api/ShipPatterns/{shipKey}/patterns/{eventName}` - Remove custom pattern
- `POST /api/ShipPatterns/{shipKey}/apply-recommendations` - Apply recommended patterns

#### Ship Information
- `GET /api/ShipPatterns/classifications` - Get all ship classifications
- `GET /api/ShipPatterns/{shipKey}/recommendations` - Get pattern recommendations

### 5. Integration with Journal System
**Modified Files**:
- `JournalMonitorService.cs` - Added ship tracking to journal event processing
- `EventMappingService.cs` - Enhanced to work with ship-specific patterns
- `Program.cs` - Registered new services in DI container

**Event Processing Flow**:
1. Journal event received → Ship tracking processes first
2. Ship change detected → Pattern library updated
3. Pattern requested → Ship-specific pattern returned (or default fallback)
4. Audio played → Using appropriate pattern for current ship

## User Experience

### Automatic Ship Detection
- **No manual configuration** - Ships automatically detected from journal
- **Seamless switching** - Patterns change when user swaps ships in-game
- **Persistent memory** - Ship patterns saved between sessions

### Ship-Specific Customization
- **Per-ship patterns** - Different FSDJump duration for Sidewinder vs Anaconda
- **Size-based defaults** - Small ships get shorter, higher-pitched patterns
- **Role-based suggestions** - Combat ships get aggressive patterns, explorers get smooth patterns

### Web Interface Features
- **Ship library view** - See all discovered ships with pattern counts
- **Current ship display** - Shows active ship with recommendations
- **Pattern editor** - Customize patterns per ship per event
- **One-click recommendations** - Apply size/role-appropriate patterns

## Example Scenarios

### Scenario 1: Ship Size Differences
**Problem**: FSDJump pattern too long for small ships, too short for large ships

**Solution**:
- **Sidewinder**: 0.7x duration (2.1s instead of 3s), +5Hz frequency
- **Anaconda**: 1.4x duration (4.2s instead of 3s), -5Hz frequency, +20% intensity

### Scenario 2: Ship Role Differences  
**Problem**: Combat ship needs aggressive patterns, explorer needs smooth patterns

**Solution**:
- **Combat ships**: Sharp pulses, high intensity, quick responses
- **Exploration ships**: Smooth buildups, gentle transitions, sustained patterns
- **Transport ships**: Heavy, substantial patterns matching cargo mass

### Scenario 3: Automatic Switching
**Sequence**:
1. User flies Sidewinder with custom short patterns
2. User docks at station and swaps to Anaconda
3. System detects `ShipyardSwap` journal event  
4. Automatically switches to Anaconda's longer, heavier patterns
5. No manual configuration needed

## Configuration Examples

### Ship Patterns File Structure
```json
{
  "ships": {
    "anaconda_My Big Ship": {
      "shipKey": "anaconda_My Big Ship",
      "shipType": "anaconda", 
      "shipName": "My Big Ship",
      "eventPatterns": {
        "FSDJump": {
          "name": "Heavy Ship Jump",
          "duration": 4200,
          "intensity": 90,
          "frequency": 35
        }
      }
    }
  },
  "currentShipKey": "anaconda_My Big Ship"
}
```

### API Usage Examples
```javascript
// Get current ship
const currentShip = await fetch('/api/ShipPatterns/current-ship').then(r => r.json());

// Set custom pattern for current ship
await fetch(`/api/ShipPatterns/${currentShip.ship.shipKey}/patterns`, {
  method: 'POST',
  body: JSON.stringify({
    eventName: 'FSDJump',
    pattern: {
      name: 'Custom Jump',
      duration: 2500,
      intensity: 75,
      frequency: 42
    }
  })
});

// Apply size-based recommendations
await fetch(`/api/ShipPatterns/${shipKey}/apply-recommendations`, {
  method: 'POST'
});
```

## Technical Benefits

### Performance
- **Lazy loading** - Ship patterns only loaded when needed
- **Efficient lookup** - O(1) pattern resolution by ship key
- **Background persistence** - Saving doesn't block audio playback

### Scalability  
- **Supports all ships** - 50+ Elite Dangerous ships classified
- **Extensible** - Easy to add new ships or pattern types
- **Future-proof** - Works with new ships added to Elite Dangerous

### Reliability
- **Graceful fallbacks** - Always has default pattern if ship-specific missing
- **Error resilience** - Corrupted ship patterns don't break audio system
- **State recovery** - Ship tracking recovers from journal events

This implementation solves the core problem of different ships needing different pattern durations and provides a scalable, user-friendly system for ship-specific haptic customization. Users will no longer experience patterns that are too long or short for their current ship, and the system automatically adapts as they switch between different vessels.