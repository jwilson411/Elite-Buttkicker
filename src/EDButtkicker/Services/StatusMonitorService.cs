using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using EDButtkicker.Configuration;

namespace EDButtkicker.Services;

/// <summary>
/// Watches Elite Dangerous Status.json for real-time flag changes and triggers haptic patterns.
/// Status.json updates every ~1 second while the game is running.
/// The bit logic lives in <see cref="StatusFlagChangeDetector"/>; this service only owns the file
/// polling, so nothing here decides what a flag edge means.
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

        try
        {
            // Wait until file exists (game may not be running yet)
            while (!File.Exists(_statusFilePath) && !stoppingToken.IsCancellationRequested)
            {
                _logger.LogDebug("Status.json not found yet, waiting...");
                await Task.Delay(2000, stoppingToken);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            _logger.LogInformation("Status.json found, beginning monitoring");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PollStatusFile(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error polling Status.json");
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: nothing left to do, and no delay to sit out.
            _logger.LogInformation("Status Monitor Service stopping");
        }
    }

    private async Task PollStatusFile(CancellationToken stoppingToken)
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
                json = await reader.ReadToEndAsync(stoppingToken);
            }

            if (string.IsNullOrWhiteSpace(json))
                return;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Flags", out var flagsElement))
                return;

            // A truncated write can leave a value of the wrong shape behind; treat it like the
            // partial-JSON case and wait for the next poll rather than throwing.
            if (flagsElement.ValueKind != JsonValueKind.Number || !flagsElement.TryGetInt64(out var flags))
                return;

            long flags2 = 0;
            if (root.TryGetProperty("Flags2", out var flags2Element) &&
                (flags2Element.ValueKind != JsonValueKind.Number || !flags2Element.TryGetInt64(out flags2)))
            {
                flags2 = 0;
            }

            // First read - just store state, don't trigger anything
            if (_previousFlags == -1)
            {
                _previousFlags = flags;
                _previousFlags2 = flags2;
                return;
            }

            if (flags == _previousFlags && flags2 == _previousFlags2)
                return;

            // Both words matter: an edge that only appears in Flags2 is still an edge.
            var statusEvents = StatusFlagChangeDetector.Detect(_previousFlags, flags, _previousFlags2, flags2);

            _logger.LogDebug(
                "Status flags changed: {OldFlags}/{OldFlags2} -> {NewFlags}/{NewFlags2} (events: {Events})",
                _previousFlags, _previousFlags2, flags, flags2, statusEvents.Count);

            // Store before playing, so a slow or failing playback cannot make the next poll
            // re-detect the same edge.
            _previousFlags = flags;
            _previousFlags2 = flags2;

            foreach (var statusEvent in statusEvents)
            {
                if (stoppingToken.IsCancellationRequested)
                    return;

                await TriggerStatusPattern(statusEvent);
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
}
