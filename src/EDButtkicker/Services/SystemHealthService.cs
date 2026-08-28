using EDButtkicker.Configuration;
using EDButtkicker.Hosting;
using Microsoft.Extensions.Logging;

namespace EDButtkicker.Services;

/// <summary>The action a user can take when an indicator is not green.</summary>
public sealed record HealthRetry(string Endpoint, string Method, string Label);

/// <summary>
/// One subsystem, its real state, and why it is in that state. <see cref="Reason"/> is always
/// filled in - an indicator that cannot say why it is red is the bug this replaces.
/// </summary>
public sealed record HealthIndicator(
    string Id,
    string Name,
    string Status,
    string Reason,
    string? Detail,
    HealthRetry? Retry);

public sealed record SystemHealthReport(
    string Status,
    DateTime GeneratedAtUtc,
    IReadOnlyList<HealthIndicator> Components);

/// <summary>
/// Builds the dashboard's health list from the subsystems themselves. Every indicator is derived
/// from that subsystem's own state - the journal watcher's attachment, the audio engine's device,
/// the loaded pattern catalog - rather than from an unrelated API call returning 200.
/// </summary>
public class SystemHealthService
{
    public const string StatusOk = "ok";
    public const string StatusPending = "pending";
    public const string StatusAttention = "attention";
    public const string StatusError = "error";
    public const string StatusOff = "off";

    /// <summary>How long a journal retry waits for the monitor to pick the request up.</summary>
    private static readonly TimeSpan JournalRetryWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan JournalRetryPoll = TimeSpan.FromMilliseconds(25);

    private readonly ILogger<SystemHealthService> _logger;
    private readonly AppSettings _settings;
    private readonly JournalMonitorStatus _journalStatus;
    private readonly AudioEngineService _audioEngine;
    private readonly IAudioDeviceCatalog _deviceCatalog;
    private readonly IJournalEventStore _eventStore;
    private readonly IPatternCatalog _patternCatalog;
    private readonly EventMappingService _eventMappings;
    private readonly TimeProvider _timeProvider;

    public SystemHealthService(
        ILogger<SystemHealthService> logger,
        AppSettings settings,
        JournalMonitorStatus journalStatus,
        AudioEngineService audioEngine,
        IAudioDeviceCatalog deviceCatalog,
        IJournalEventStore eventStore,
        IPatternCatalog patternCatalog,
        EventMappingService eventMappings,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _settings = settings;
        _journalStatus = journalStatus;
        _audioEngine = audioEngine;
        _deviceCatalog = deviceCatalog;
        _eventStore = eventStore;
        _patternCatalog = patternCatalog;
        _eventMappings = eventMappings;
        _timeProvider = timeProvider;
    }

    public SystemHealthReport GetReport()
    {
        var components = new List<HealthIndicator>
        {
            GetJournalIndicator(),
            GetAudioIndicator(),
            GetPatternIndicator(),
            GetVoiceIndicator(),
            GetWebIndicator()
        };

        return new SystemHealthReport(Overall(components), _timeProvider.GetUtcNow().UtcDateTime, components);
    }

    public HealthIndicator? GetIndicator(string id) =>
        GetReport().Components.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="id"/> names a subsystem a retry can actually act on.</summary>
    public static bool CanRetry(string id) =>
        string.Equals(id, "journal", StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, "audio", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the retry for one subsystem and returns its indicator afterwards, so the caller reports
    /// the state the retry produced instead of an optimistic "retrying...".
    /// </summary>
    public async Task<HealthIndicator?> RetryAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.Equals(id, "journal", StringComparison.OrdinalIgnoreCase))
        {
            await RetryJournalAsync(cancellationToken);
            return GetIndicator("journal");
        }

        if (string.Equals(id, "audio", StringComparison.OrdinalIgnoreCase))
        {
            var opened = _audioEngine.RetryInitialization();
            _logger.LogInformation("Audio retry requested from the health API, device opened: {Opened}", opened);
            return GetIndicator("audio");
        }

        return null;
    }

    private async Task RetryJournalAsync(CancellationToken cancellationToken)
    {
        var before = _journalStatus.Current;
        _journalStatus.RequestRecheck();
        _logger.LogInformation("Journal re-check requested from the health API");

        // Give a running monitor a moment to act, so the response reflects the retry. When no
        // monitor is running the wait simply expires and the indicator stays honest.
        var deadline = _timeProvider.GetUtcNow() + JournalRetryWindow;

        while (_timeProvider.GetUtcNow() < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (_journalStatus.Current != before)
            {
                return;
            }

            await Task.Delay(JournalRetryPoll, _timeProvider, cancellationToken);
        }
    }

    private HealthIndicator GetJournalIndicator()
    {
        var snapshot = _journalStatus.Current;
        var path = snapshot.Path ?? _settings.EliteDangerous.JournalPath;
        var retry = new HealthRetry("/api/health/journal/retry", "POST", "Re-check journal folder");
        var detail = BuildJournalDetail(path);

        return snapshot.State switch
        {
            JournalWatchState.Watching when snapshot.ActiveFile != null =>
                new HealthIndicator("journal", "Journal Monitor", StatusOk, snapshot.Reason, detail, retry),

            // Attached, but Elite Dangerous has not produced a journal file yet: not an error, and
            // not "connected" either.
            JournalWatchState.Watching =>
                new HealthIndicator("journal", "Journal Monitor", StatusAttention, snapshot.Reason, detail, retry),

            JournalWatchState.Waiting =>
                new HealthIndicator("journal", "Journal Monitor", StatusAttention, snapshot.Reason, detail, retry),

            JournalWatchState.Faulted =>
                new HealthIndicator("journal", "Journal Monitor", StatusError, snapshot.Reason, detail, retry),

            JournalWatchState.Stopped =>
                new HealthIndicator("journal", "Journal Monitor", StatusAttention, snapshot.Reason, detail, retry),

            _ => new HealthIndicator("journal", "Journal Monitor", StatusPending, snapshot.Reason, detail, retry)
        };
    }

    private string BuildJournalDetail(string? path)
    {
        var folder = string.IsNullOrWhiteSpace(path) ? "No journal folder configured" : $"Folder: {path}";
        var events = $"{_eventStore.Count} event(s) seen this session";
        var last = _eventStore.LastTimestamp is { } timestamp
            ? $"last at {timestamp:yyyy-MM-dd HH:mm:ss}"
            : "no events yet";

        return $"{folder} | {events}, {last}";
    }

    private HealthIndicator GetAudioIndicator()
    {
        var status = _audioEngine.GetStatus();
        var retry = new HealthRetry("/api/health/audio/retry", "POST", "Retry audio device");
        var devices = _deviceCatalog.GetDevices();
        var configuredName = status.ConfiguredDeviceName;
        var detail = $"{devices.Count} output device(s) available | configured: " +
            (string.IsNullOrWhiteSpace(configuredName) ? "system default" : configuredName);

        if (status.InitializationFailed)
        {
            var reason = string.IsNullOrWhiteSpace(status.LastError)
                ? "The audio output device could not be opened."
                : $"The audio output device could not be opened: {status.LastError}";

            return new HealthIndicator("audio", "Audio Engine", StatusError, reason, detail, retry);
        }

        var configuredIsMissing = !string.IsNullOrWhiteSpace(configuredName)
            && !devices.Any(d => string.Equals(d.Name, configuredName, StringComparison.OrdinalIgnoreCase));

        if (status.Initialized)
        {
            var reason = configuredIsMissing
                ? $"Playing on a fallback device: the saved output '{configuredName}' is not connected."
                : $"Output open on {(string.IsNullOrWhiteSpace(configuredName) ? "the system default device" : configuredName)}.";

            return new HealthIndicator(
                "audio",
                "Audio Engine",
                configuredIsMissing ? StatusAttention : StatusOk,
                reason,
                detail,
                retry);
        }

        if (configuredIsMissing)
        {
            return new HealthIndicator(
                "audio",
                "Audio Engine",
                StatusAttention,
                $"The saved output '{configuredName}' is not connected; the system default will be used instead.",
                detail,
                retry);
        }

        return new HealthIndicator(
            "audio",
            "Audio Engine",
            StatusPending,
            "No audio device has been opened yet - run the audio test to open one.",
            detail,
            retry);
    }

    private HealthIndicator GetPatternIndicator()
    {
        var retry = new HealthRetry("/api/PatternFiles/reload", "POST", "Reload pattern files");

        try
        {
            var eventPatterns = _eventMappings.GetAllDefaultPatterns().Count;
            var shipTypes = _patternCatalog.GetAllShipTypes().Count;
            var detail = $"{eventPatterns} event pattern(s), {shipTypes} ship-specific librar(ies)";

            if (eventPatterns == 0)
            {
                return new HealthIndicator(
                    "patterns",
                    "Haptic Patterns",
                    StatusError,
                    "No event patterns are loaded, so no event can produce haptics.",
                    detail,
                    retry);
            }

            return new HealthIndicator(
                "patterns",
                "Haptic Patterns",
                StatusOk,
                $"{eventPatterns} event pattern(s) loaded.",
                detail,
                retry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading the pattern catalog for the health report");

            return new HealthIndicator(
                "patterns",
                "Haptic Patterns",
                StatusError,
                $"The pattern catalog could not be read: {ex.Message}",
                null,
                retry);
        }
    }

    private HealthIndicator GetVoiceIndicator()
    {
        // VoiceFeedbackService is not part of the running service graph, so nothing is ever spoken.
        // The dashboard used to show this as "online"; saying it is off is the truthful reading.
        var contextualVoice = _settings.ContextualIntelligence?.EnableContextualVoice == true;

        return new HealthIndicator(
            "voice",
            "Voice Feedback",
            StatusOff,
            "Voice feedback is not running: no voice service is hosted in this build.",
            contextualVoice
                ? "Contextual voice is enabled in settings but has no service to drive it."
                : "Contextual voice is disabled in settings.",
            Retry: null);
    }

    private static HealthIndicator GetWebIndicator() =>
        new(
            "web",
            "Web Interface",
            StatusOk,
            $"Serving this page on http://localhost:{WebUiConfiguration.Port} (loopback only).",
            "The interface is reachable from this machine only.",
            Retry: null);

    private static string Overall(IEnumerable<HealthIndicator> components)
    {
        var statuses = components.Select(c => c.Status).ToList();

        if (statuses.Contains(StatusError)) return StatusError;
        if (statuses.Contains(StatusAttention)) return StatusAttention;
        if (statuses.Contains(StatusPending)) return StatusPending;

        return StatusOk;
    }
}
