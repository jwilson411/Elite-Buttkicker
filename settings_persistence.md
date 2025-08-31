# User Settings Persistence Implementation

## Overview
Implemented comprehensive user settings persistence so users can save their configurations and have them automatically loaded on startup.

## Features Implemented

### 1. UserSettingsService
**Location**: `src/EDButtkicker/Services/UserSettingsService.cs`

**Key Features**:
- **File-based persistence** using JSON in user's AppData folder
- **Settings path**: `%APPDATA%\EDButtkicker\user-settings.json`
- **Automatic directory creation**
- **Graceful error handling** with fallback to defaults
- **Type-safe serialization** using System.Text.Json

**Core Methods**:
```csharp
// Load user preferences from disk
Task<UserPreferences> LoadUserPreferencesAsync()

// Save user preferences to disk  
Task SaveUserPreferencesAsync(UserPreferences preferences)

// Apply preferences to runtime app settings
void ApplyUserPreferencesToAppSettings(UserPreferences preferences, AppSettings appSettings)

// Create preferences from current app settings
UserPreferences CreatePreferencesFromAppSettings(AppSettings appSettings)
```

### 2. UserPreferences Data Model
**Persisted Settings**:
- **Audio Configuration**: Device ID, device name, max intensity, default frequency
- **Journal Settings**: Path, monitor latest only flag
- **Contextual Intelligence**: All AI-related preferences
- **Metadata**: Last saved timestamp, version info

**Example user-settings.json**:
```json
{
  "audioDeviceId": 1,
  "audioDeviceName": "ButtKicker USB Audio Device",
  "maxIntensity": 85,
  "defaultFrequency": 35,
  "journalPath": "C:\\Users\\User\\Saved Games\\Frontier Developments\\Elite Dangerous",
  "monitorLatestOnly": true,
  "contextualIntelligence": {
    "enabled": false,
    "enableAdaptiveIntensity": true,
    "enablePredictivePatterns": true,
    "enableContextualVoice": false
  },
  "lastSaved": "2024-08-31T15:30:00Z",
  "version": "1.0.0"
}
```

### 3. Startup Integration
**Modified Program.cs**:
- **UserSettingsService** registered in DI container
- **LoadAndApplyUserSettings** method loads saved preferences on startup
- **Fallback behavior** to auto-configuration if no settings exist
- **Debug mode** shows loaded settings information

**Startup Flow**:
1. Check if user settings file exists
2. If exists: Load and apply to AppSettings
3. If not exists: Use auto-configuration defaults
4. Display appropriate status messages

### 4. Web API Endpoints
**Location**: `src/EDButtkicker/Controllers/UserSettingsApiController.cs`

**Available Endpoints**:

#### GET `/api/UserSettings`
Returns current user preferences from disk

#### POST `/api/UserSettings/save`
Saves new user preferences
```json
{
  "audioDeviceId": 1,
  "audioDeviceName": "ButtKicker",
  "maxIntensity": 85,
  "journalPath": "custom/path"
}
```

#### POST `/api/UserSettings/reset`
Deletes saved settings file and resets to defaults

#### GET `/api/UserSettings/current`
Returns currently active settings (runtime AppSettings values)

### 5. Debug Mode Enhancements
When running with `--debug` flag:

**If Settings Exist**:
```
💾 Loaded saved user settings:
----------------------------
Settings file: C:\Users\User\AppData\Roaming\EDButtkicker\user-settings.json  
Audio Device: ButtKicker USB Audio Device (ID: 1)
Journal Path: C:\Users\User\Saved Games\Frontier Developments\Elite Dangerous
Last Saved: 2024-08-31 15:30:00
```

**If No Settings**:
```
🏠 No saved settings found - using defaults
Settings will be saved when changed via web interface
```

## User Experience

### First Run
1. Application starts with system defaults
2. User opens http://localhost:5000
3. User configures preferred audio device
4. Settings automatically saved to AppData

### Subsequent Runs  
1. Application loads saved settings
2. User's preferred device automatically selected
3. All customizations restored
4. Immediate ready-to-use state

## Benefits

### For Users
- **No reconfiguration** needed between sessions
- **Persistent customizations** across app restarts
- **Backup/restore** possible by copying settings file
- **Reset to defaults** option available

### For Testing (Your Friend)
- **Consistent test environment** across sessions  
- **Quick device switching** with persistent memory
- **Debug visibility** into what settings are loaded
- **Easy troubleshooting** with clear settings path

### For Distribution
- **Professional behavior** like commercial applications
- **User-specific settings** don't interfere with other users
- **Roaming profile support** (settings in AppData\Roaming)
- **Clean uninstall** (settings separate from application)

## Implementation Notes

### Error Handling
- **Corrupted settings file**: Falls back to defaults, logs error
- **Permission issues**: Continues with defaults, logs warning  
- **Missing directory**: Auto-creates directory structure
- **Invalid JSON**: Deserializes what it can, uses defaults for rest

### Performance
- **Asynchronous I/O** for file operations
- **Lazy loading** only when needed
- **Memory efficient** JSON serialization
- **Fast startup** with minimal overhead

### Security
- **User-specific location** prevents privilege escalation
- **No sensitive data** stored in settings file
- **Standard JSON format** for transparency
- **Validation** of loaded values

## Usage Examples

### JavaScript Frontend (Web UI)
```javascript
// Load current settings
const settings = await fetch('/api/UserSettings/current').then(r => r.json());

// Save new audio device
await fetch('/api/UserSettings/save', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    audioDeviceId: 1,
    audioDeviceName: 'ButtKicker USB Audio Device',
    maxIntensity: 85
  })
});

// Reset to defaults
await fetch('/api/UserSettings/reset', { method: 'POST' });
```

This implementation provides a robust, user-friendly settings persistence system that will greatly improve the user experience and make testing much more efficient!