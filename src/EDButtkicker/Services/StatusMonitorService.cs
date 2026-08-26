using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using EDButtkicker.Configuration;

namespace EDButtkicker.Services;

/// <summary>
/// Watches Elite Dangerous Status.json for real-time flag changes and triggers haptic patterns.
/// Status.json updates every ~1 second while the game is running.
/// </summary>
public class StatusMonitorService : BackgroundService
{
	private readonly ILogger<StatusMonitorService> _logger;
	private readonly AppSettings _settings;
	private readonly AudioEngineService _audioEngine;
	private readonly EventMappingService _eventMapping;

	// Path to Status.json alongside the journal files
	private string _statusFilePath = string.Empty;

	// Track previous flags to detect changes
	private long _previousFlags = -1;
	private long _previousFlags2 = -1;

	// Polling interval - Status.json updates ~1s so 250ms gives responsive detection
	private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

	// ED Status.json Flags bit definitions
	[Flags]
	private enum StatusFlags : long
	{
		Docked              = 1 << 0,   // 1
		Landed              = 1 << 1,   // 2
		LandingGearDown     = 1 << 2,   // 4
		ShieldsUp           = 1 << 3,   // 8
		Supercruise         = 1 << 4,   // 16
		FlightAssistOff     = 1 << 5,   // 32
		HardpointsDeployed  = 1 << 6,   // 64
		InWing              = 1 << 7,   // 128
		LightsOn            = 1 << 8,   // 256 - was bit 9 previously
		CargoScoopDeployed  = 1 << 9,   // 512
		SilentRunning       = 1 << 10,  // 1024
		ScoopingFuel        = 1 << 11,  // 2048
		FsdMassLocked       = 1 << 16,  // 65536
		FsdCharging         = 1 << 17,  // 131072
		FsdCooldown         = 1 << 18,  // 262144
		LowFuel             = 1 << 19,  // 524288
		Overheating         = 1 << 20,  // 1048576
		HaveMission         = 1 << 21,
		Interdicted         = 1 << 23,
		InMainShip          = 1 << 24,
		InFighter           = 1 << 25,
		InSRV               = 1 << 26,
		NightVision         = 1 << 28,
	}

	public StatusMonitorService(
		ILogger<StatusMonitorService> logger,
		AppSettings settings,
		AudioEngineService audioEngine,
		EventMappingService eventMapping)
	{
		_logger = logger;
		_settings = settings;
		_audioEngine = audioEngine;
		_eventMapping = eventMapping;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		_logger.LogInformation("Starting Status Monitor Service");

		// Resolve Status.json path from journal path
		var journalDir = _settings.EliteDangerous.JournalPath;
		_statusFilePath = Path.Combine(journalDir, "Status.json");

		_logger.LogInformation("Watching Status.json at: {Path}", _statusFilePath);

		// Wait until file exists (game may not be running yet)
		while (!File.Exists(_statusFilePath) && !stoppingToken.IsCancellationRequested)
		{
			_logger.LogDebug("Status.json not found yet, waiting...");
			await Task.Delay(2000, stoppingToken);
		}

		_logger.LogInformation("Status.json found, beginning monitoring");

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await PollStatusFile();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error polling Status.json");
			}

			await Task.Delay(PollInterval, stoppingToken);
		}
	}

	private async Task PollStatusFile()
	{
		if (!File.Exists(_statusFilePath))
			return;

		try
		{
			// Read with shared access since the game also writes this file
			string json;
			using (var stream = new FileStream(_statusFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
			using (var reader = new StreamReader(stream))
			{
				json = await reader.ReadToEndAsync();
			}

			if (string.IsNullOrWhiteSpace(json))
				return;

			var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;

			if (!root.TryGetProperty("Flags", out var flagsElement))
				return;

			var flags = flagsElement.GetInt64();
			var flags2 = root.TryGetProperty("Flags2", out var flags2Element)
				? flags2Element.GetInt64()
				: 0;

			// First read - just store state, don't trigger anything
			if (_previousFlags == -1)
			{
				_previousFlags = flags;
				_previousFlags2 = flags2;
				return;
			}

			// Detect changes and trigger patterns
			if (flags != _previousFlags || flags2 != _previousFlags2)
			{
				await ProcessFlagChanges(_previousFlags, flags, _previousFlags2, flags2);
				_previousFlags = flags;
				_previousFlags2 = flags2;
			}
		}
		catch (JsonException)
		{
			// Status.json can be partially written - just skip this poll
		}
		catch (IOException)
		{
			// File locked momentarily - skip this poll
		}
	}

	private async Task ProcessFlagChanges(long oldFlags, long newFlags, long oldFlags2, long newFlags2)
	{
		var changed = oldFlags ^ newFlags;
		if (changed == 0) return;

		_logger.LogDebug("Status flags changed: {OldFlags} -> {NewFlags} (changed bits: {Changed})",
			oldFlags, newFlags, changed);

		// Landing Gear
		if (HasBitChanged(changed, (long)StatusFlags.LandingGearDown))
		{
			bool gearDown = HasFlag(newFlags, (long)StatusFlags.LandingGearDown);
			_logger.LogInformation("Landing gear {State}", gearDown ? "deployed" : "retracted");

			if (gearDown)
				await Task.Delay(2000); // wait for gear to start moving before feedback

			await TriggerStatusPattern(gearDown ? "LandingGearDown" : "LandingGearUp");
		}

		// Hardpoints
		if (HasBitChanged(changed, (long)StatusFlags.HardpointsDeployed))
		{
			bool deployed = HasFlag(newFlags, (long)StatusFlags.HardpointsDeployed);
			_logger.LogInformation("Hardpoints {State}", deployed ? "deployed" : "retracted");
			await TriggerStatusPattern(deployed ? "HardpointsDeployed" : "HardpointsRetracted");
		}

		// Cargo Scoop
		if (HasBitChanged(changed, (long)StatusFlags.CargoScoopDeployed))
		{
			bool deployed = HasFlag(newFlags, (long)StatusFlags.CargoScoopDeployed);
			_logger.LogInformation("Cargo scoop {State}", deployed ? "deployed" : "retracted");
			await TriggerStatusPattern(deployed ? "CargoScoopDeployed" : "CargoScoopRetracted");
		}

		// Silent Running
		if (HasBitChanged(changed, (long)StatusFlags.SilentRunning))
		{
			bool enabled = HasFlag(newFlags, (long)StatusFlags.SilentRunning);
			_logger.LogInformation("Silent running {State}", enabled ? "enabled" : "disabled");
			await TriggerStatusPattern(enabled ? "SilentRunningOn" : "SilentRunningOff");
		}

		// FSD Charging
		if (HasBitChanged(changed, (long)StatusFlags.FsdCharging))
		{
			bool charging = HasFlag(newFlags, (long)StatusFlags.FsdCharging);
			if (charging)
			{
				_logger.LogInformation("FSD charging");
				await TriggerStatusPattern("FsdCharging");
			}
		}

		// FSD Cooldown
		if (HasBitChanged(changed, (long)StatusFlags.FsdCooldown))
		{
			bool cooldown = HasFlag(newFlags, (long)StatusFlags.FsdCooldown);
			if (cooldown)
			{
				_logger.LogInformation("FSD cooldown started");
				await TriggerStatusPattern("FsdCooldown");
			}
		}

		// Low Fuel warning
		if (HasBitChanged(changed, (long)StatusFlags.LowFuel))
		{
			bool lowFuel = HasFlag(newFlags, (long)StatusFlags.LowFuel);
			if (lowFuel)
			{
				_logger.LogInformation("Low fuel warning");
				await TriggerStatusPattern("LowFuel");
			}
		}

		// Overheating
		if (HasBitChanged(changed, (long)StatusFlags.Overheating))
		{
			bool overheating = HasFlag(newFlags, (long)StatusFlags.Overheating);
			if (overheating)
			{
				_logger.LogInformation("Ship overheating");
				await TriggerStatusPattern("Overheating");
			}
		}

		// Night Vision
		if (HasBitChanged(changed, (long)StatusFlags.NightVision))
		{
			bool enabled = HasFlag(newFlags, (long)StatusFlags.NightVision);
			_logger.LogInformation("Night vision {State}", enabled ? "on" : "off");
			await TriggerStatusPattern(enabled ? "NightVisionOn" : "NightVisionOff");
		}
	}

	private async Task TriggerStatusPattern(string eventType)
	{
		try
		{
			var pattern = _eventMapping.GetDefaultPatternForEvent(eventType);
			if (pattern == null)
			{
				_logger.LogDebug("No pattern mapped for status event: {EventType}", eventType);
				return;
			}

			_logger.LogDebug("Triggering haptic pattern for status event: {EventType}", eventType);
			await _audioEngine.PlayHapticPattern(pattern);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error triggering haptic pattern for status event: {EventType}", eventType);
		}
	}

	private static bool HasBitChanged(long changed, long flag) => (changed & flag) != 0;
	private static bool HasFlag(long flags, long flag) => (flags & flag) != 0;
}
