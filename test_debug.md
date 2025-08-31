# Debug Logging Implementation

## Features Added

### 1. Enhanced Configuration (appsettings.json)
- Added detailed logging levels for specific components
- Added debug-specific configuration options
- Enhanced console logging with timestamps

### 2. Command Line Debug Mode
- Added `--debug` or `-d` flag support
- Added `--help` or `-h` flag for usage information
- Conditional debug output throughout application

### 3. Detailed Audio Engine Logging
- System audio device enumeration with full details
- Audio device validation and error reporting
- WaveOut configuration logging
- Pattern playback state tracking
- Comprehensive error analysis

### 4. Device Selection Debug Information
- WASAPI device enumeration
- WaveOut device comparison
- Device capability inspection
- Default device identification
- Selection confirmation with full device details

## Usage Instructions

To run the application with debug logging:

```bash
# Enable debug mode
EDButtkicker --debug

# Or short form
EDButtkicker -d

# Show help
EDButtkicker --help
```

## Debug Information Captured

When debug mode is enabled, the following information is logged:

1. **System Audio Information**
   - Complete list of available audio devices
   - Device capabilities and states  
   - Default device identification
   - WASAPI vs WaveOut device mapping

2. **Device Selection Process**
   - Device enumeration details
   - User selection confirmation
   - Device configuration validation
   - Availability warnings

3. **Audio Engine Operations**
   - Initialization steps and validation
   - Pattern playback with full details
   - Mixer operations and state
   - Error diagnosis with context

4. **Troubleshooting Information**
   - Exception details and stack traces
   - System state at error time
   - Configuration that might cause issues
   - Recovery attempts and results

## Example Debug Output

```
Elite Dangerous Buttkicker Extension
====================================
🔍 DEBUG MODE ENABLED
Detailed logging will be displayed

[DEBUG] Enumerating audio devices...
[DEBUG] Found 3 WASAPI render devices
[DEBUG] System default device: 'Speakers (Realtek Audio)' (ID: {0.0.0.00000000}...)
[DEBUG] Device 0: 'Speakers (Realtek Audio)' - State: Active, Default: True
[DEBUG] Device 1: 'ButtKicker (USB Audio Device)' - State: Active, Default: False
[DEBUG] Device 2: 'Headphones (Bluetooth)' - State: Active, Default: False
[DEBUG] WaveOut device count: 3
[DEBUG] WaveOut Device 0: 'Speakers (Realtek Audio)' - Channels: 2
[DEBUG] WaveOut Device 1: 'ButtKicker (USB Audio Device)' - Channels: 2
[DEBUG] WaveOut Device 2: 'Headphones (Bluetooth)' - Channels: 2
[DEBUG] Returning 3 available devices
```

This debug logging will help identify:
- Audio device routing issues (like your friend experienced)
- NAudio initialization problems
- Device selection conflicts
- Pattern playback failures
- System-specific audio configuration issues

## For Your Friend's Issue

The debug logging should reveal:
1. Whether the ButtKicker device is properly detected
2. If the device selection is working correctly
3. Whether NAudio is successfully routing to the device
4. If there are any permission or driver issues
5. Whether the audio is being generated but not reaching the device

This will provide the detailed logs needed to analyze and fix the audio routing problems.