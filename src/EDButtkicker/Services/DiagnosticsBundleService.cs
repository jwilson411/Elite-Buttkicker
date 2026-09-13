using System.Runtime.InteropServices;
using System.Text.Json;
using EDButtkicker.Configuration;
using Microsoft.Extensions.Logging;

namespace EDButtkicker.Services;

/// <summary>
/// Builds the one artifact a maintainer can ask a stranger for: everything needed to triage a
/// hardware or startup report, and nothing that belongs to the reporter's save game.
///
/// Two rules make that true, and both are structural rather than a filter applied at the end:
/// every field is named here by hand (so a new setting does not start exporting itself), and every
/// string that came from somewhere else - a health reason, an audio error, a logged failure - goes
/// through <see cref="DiagnosticsRedactor"/>. Journal <em>contents</em> have no path into the bundle
/// at all: the watcher section carries event names and counts, which is what
/// <c>CONTRIBUTING.md</c> already asks reporters for.
///
/// Nothing here uploads, shares or writes anything on its own.
/// <see cref="PreviewAndSaveAsync"/> is the only way a file appears, and it asks first.
/// </summary>
public class DiagnosticsBundleService
{
    /// <summary>Bumped when the bundle's shape changes, so an old attachment is still readable.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Distinct journal event names reported, most frequent first.</summary>
    public const int MaxEventTypeEntries = 30;

    /// <summary>
    /// A settings file larger than this is not read for unknown keys. It would not be a settings
    /// file, and a bundle must not be a way to make the app read an arbitrarily large one.
    /// </summary>
    private const int MaxUserSettingsBytes = 256 * 1024;

    /// <summary>
    /// Carried inside every bundle. This is the promise the preview makes to the user and the list a
    /// maintainer reads before asking for "just the journal file too".
    /// </summary>
    private static readonly string[] ExcludedByDesign =
    {
        "Raw journal file contents - no journal line, and no field from one, is read into this bundle.",
        "Commander-identifying values - commander name, credit balances, ship IDs, systems and stations visited.",
        "Full filesystem paths - home, settings, temp and install directories are replaced with placeholders.",
        "Account and machine names - the Windows/Linux user name and host name are replaced with placeholders.",
        "Secrets - values of configuration keys this build does not know about are never copied, only their names.",
        "Network activity - this file is written locally and is never uploaded or shared by the application."
    };

    private readonly ILogger<DiagnosticsBundleService> _logger;
    private readonly AppSettings _settings;
    private readonly JournalMonitorStatus _journalStatus;
    private readonly AudioEngineService _audioEngine;
    private readonly IAudioDeviceCatalog _deviceCatalog;
    private readonly IJournalEventStore _eventStore;
    private readonly SystemHealthService _health;
    private readonly UserSettingsService _userSettings;
    private readonly RecentErrorLog _recentErrors;
    private readonly DiagnosticsRedactor _redactor;
    private readonly TimeProvider _timeProvider;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public DiagnosticsBundleService(
        ILogger<DiagnosticsBundleService> logger,
        AppSettings settings,
        JournalMonitorStatus journalStatus,
        AudioEngineService audioEngine,
        IAudioDeviceCatalog deviceCatalog,
        IJournalEventStore eventStore,
        SystemHealthService health,
        UserSettingsService userSettings,
        RecentErrorLog recentErrors,
        DiagnosticsRedactor redactor,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _settings = settings;
        _journalStatus = journalStatus;
        _audioEngine = audioEngine;
        _deviceCatalog = deviceCatalog;
        _eventStore = eventStore;
        _health = health;
        _userSettings = userSettings;
        _recentErrors = recentErrors;
        _redactor = redactor;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Reads the live state of every subsystem into a sanitized bundle. Opens no audio device,
    /// touches no journal file, and writes nothing.
    /// </summary>
    public DiagnosticsBundle Build() =>
        new(
            SchemaVersion,
            _timeProvider.GetUtcNow().UtcDateTime,
            BuildApplicationInfo(),
            BuildConfigurationInfo(),
            BuildAudioEndpoints(),
            BuildAudioBackend(),
            BuildJournalWatcher(),
            BuildHealth(),
            BuildRecentErrors(),
            ExcludedByDesign);

    /// <summary>The bundle as the file would contain it - and as the preview shows it.</summary>
    public string Render(DiagnosticsBundle bundle) => JsonSerializer.Serialize(bundle, _jsonOptions);

    /// <summary>
    /// Where a bundle is written when the user does not name a file: beside the other per-user state,
    /// with a timestamp so two reports from one session do not overwrite each other.
    /// </summary>
    public string SuggestBundlePath() => Path.Combine(
        _userSettings.SettingsDirectory,
        $"diagnostics-bundle-{_timeProvider.GetUtcNow().UtcDateTime:yyyyMMdd-HHmmss}.json");

    /// <summary>
    /// Turns what the user asked for into a file path. A directory is accepted and gets the suggested
    /// file name inside it, because "put it in this folder" is the obvious thing to type.
    /// </summary>
    public string ResolveOutputPath(string? requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return SuggestBundlePath();
        }

        var trimmed = requestedPath.Trim();

        return Directory.Exists(trimmed)
            ? Path.Combine(trimmed, Path.GetFileName(SuggestBundlePath()))
            : Path.GetFullPath(trimmed);
    }

    /// <summary>
    /// Shows the user the exact bytes that would be written, then writes them only if they say yes.
    /// The preview is the file - not a summary of it - so "I did not know that was in there" cannot
    /// happen, and a declined prompt leaves nothing behind.
    /// </summary>
    public async Task<DiagnosticsBundleSaveResult> PreviewAndSaveAsync(
        string? requestedPath,
        TextWriter output,
        TextReader input,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveOutputPath(requestedPath);
        var bundle = Build();
        var json = Render(bundle);

        output.WriteLine("Diagnostics support bundle - preview");
        output.WriteLine("====================================");
        output.WriteLine();
        output.WriteLine("This is the complete contents of the bundle. Nothing has been written yet.");
        output.WriteLine();
        output.WriteLine(json);
        output.WriteLine();
        output.WriteLine("Deliberately excluded:");

        foreach (var exclusion in bundle.ExcludedByDesign)
        {
            output.WriteLine($"  • {exclusion}");
        }

        output.WriteLine();
        output.WriteLine($"Would be written to: {path}");
        output.Write("Write this file? Type 'yes' to confirm, anything else to cancel: ");
        output.Flush();

        var answer = await input.ReadLineAsync(cancellationToken);

        if (!IsAffirmative(answer))
        {
            _logger.LogInformation("Diagnostics bundle declined at the confirmation prompt; nothing was written");
            output.WriteLine();
            output.WriteLine("Cancelled - no file was written.");

            return new DiagnosticsBundleSaveResult(false, path, "Cancelled at the confirmation prompt.");
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(path, json, cancellationToken);

            // The local path is printed but never put in the bundle: the user needs to find the file,
            // the issue thread does not need their folder layout.
            _logger.LogInformation("Diagnostics bundle written after explicit confirmation");
            output.WriteLine();
            output.WriteLine($"Saved: {path}");
            output.WriteLine("Attach that file to your GitHub issue.");

            return new DiagnosticsBundleSaveResult(true, path, "Written after explicit confirmation.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write the diagnostics bundle");
            output.WriteLine();
            output.WriteLine($"Could not write the bundle: {ex.Message}");

            return new DiagnosticsBundleSaveResult(false, path, $"Could not write the file: {ex.Message}");
        }
    }

    private static bool IsAffirmative(string? answer)
    {
        var trimmed = answer?.Trim();

        return string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "y", StringComparison.OrdinalIgnoreCase);
    }

    private static DiagnosticsApplicationInfo BuildApplicationInfo() =>
        new(
            "EDButtkicker",
            BuildVersion.Current,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString());

    private DiagnosticsConfigurationInfo BuildConfigurationInfo()
    {
        var journalPath = _settings.EliteDangerous.JournalPath;
        var contextual = _settings.ContextualIntelligence;

        return new DiagnosticsConfigurationInfo(
            _settings.Audio.SampleRate,
            _settings.Audio.BufferSize,
            _settings.Audio.DefaultFrequency,
            _settings.Audio.MaxIntensity,
            // A device name is the point of a hardware report, but it is still user-supplied text.
            _redactor.Redact(_settings.Audio.AudioDeviceName) ?? string.Empty,
            _settings.Audio.AudioDeviceEndpointId,
            _settings.Audio.AudioDeviceId,
            _redactor.RedactPath(journalPath),
            !string.IsNullOrWhiteSpace(journalPath) && SafeDirectoryExists(journalPath),
            _settings.EliteDangerous.MonitorLatestOnly,
            contextual is null
                ? null
                : new DiagnosticsContextualIntelligenceInfo(
                    contextual.Enabled,
                    contextual.LearningRate,
                    contextual.PredictionThreshold,
                    contextual.EnableAdaptiveIntensity,
                    contextual.EnablePredictivePatterns,
                    contextual.EnableContextualVoice,
                    contextual.LogContextAnalysis),
            BuildUserSettingsInfo());
    }

    /// <summary>
    /// Describes the saved settings file without copying it. Keys this build does not bind are named
    /// and nothing more, which is how a credential added to that file later stays out of a bundle.
    /// </summary>
    private DiagnosticsUserSettingsInfo BuildUserSettingsInfo()
    {
        var path = _userSettings.GetUserSettingsPath();
        var exists = _userSettings.UserSettingsExist();

        if (!exists)
        {
            return new DiagnosticsUserSettingsInfo(false, _redactor.RedactPath(path), null, null, Array.Empty<string>());
        }

        DateTime? lastSaved = null;
        string? version = null;
        var unknownKeys = new List<string>();

        try
        {
            var file = new FileInfo(path);

            if (file.Length <= MaxUserSettingsBytes)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));

                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var known = KnownUserSettingsKeys();

                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (!known.Contains(property.Name))
                        {
                            // The name, never the value - and a name that reads like a credential is
                            // labelled so a maintainer can see why nothing was reported for it.
                            unknownKeys.Add(DiagnosticsRedactor.IsSensitiveKey(property.Name)
                                ? $"{property.Name} [looks like a credential - value withheld]"
                                : property.Name);
                            continue;
                        }

                        // Only these two values of a known key are ever copied, so a settings property
                        // added later cannot start appearing in bundles by itself.
                        if (property.NameEquals("lastSaved") && property.Value.TryGetDateTime(out var saved))
                        {
                            lastSaved = saved.ToUniversalTime();
                        }
                        else if (property.NameEquals("version") && property.Value.ValueKind == JsonValueKind.String)
                        {
                            version = _redactor.Redact(property.Value.GetString());
                        }
                    }
                }
            }
            else
            {
                unknownKeys.Add($"[settings file larger than {MaxUserSettingsBytes} bytes was not inspected]");
            }
        }
        catch (Exception ex)
        {
            // A damaged settings file is itself useful triage information, so the bundle says so
            // rather than failing to build.
            _logger.LogWarning(ex, "Could not inspect the user settings file for the diagnostics bundle");
            unknownKeys.Add("[settings file could not be read]");
        }

        return new DiagnosticsUserSettingsInfo(
            true,
            _redactor.RedactPath(path),
            lastSaved,
            version,
            unknownKeys.Select(key => _redactor.Redact(key)!).ToList());
    }

    /// <summary>
    /// The settings keys this build understands, taken from the type it binds, so the "unknown keys"
    /// list cannot drift as <see cref="UserPreferences"/> grows.
    /// </summary>
    private static HashSet<string> KnownUserSettingsKeys() =>
        typeof(UserPreferences)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<DiagnosticsAudioEndpoint> BuildAudioEndpoints() =>
        _deviceCatalog.GetDevices()
            .Select(device => new DiagnosticsAudioEndpoint(
                device.DeviceId,
                device.EndpointId,
                _redactor.Redact(device.Name) ?? string.Empty,
                device.Driver,
                device.Channels,
                device.IsDefault,
                device.IsAvailable))
            .ToList();

    private DiagnosticsAudioBackend BuildAudioBackend()
    {
        var status = _audioEngine.GetStatus();

        return new DiagnosticsAudioBackend(
            status.Initialized,
            status.InitializationFailed,
            _redactor.Redact(status.LastError),
            _redactor.Redact(status.ConfiguredDeviceName),
            _redactor.Redact(status.ActiveDeviceName),
            status.ActiveEndpointId,
            status.Backend,
            status.OpenedAtUtc,
            _redactor.Redact(status.LastPlaybackError),
            status.LastPlaybackAtUtc,
            status.ActiveEffects);
    }

    private DiagnosticsJournalWatcher BuildJournalWatcher()
    {
        var snapshot = _journalStatus.Current;

        return new DiagnosticsJournalWatcher(
            snapshot.State.ToString(),
            _redactor.RedactPath(snapshot.Path ?? _settings.EliteDangerous.JournalPath),
            _redactor.Redact(snapshot.Reason) ?? string.Empty,
            // A journal file name is a timestamp ("Journal.2026-09-13T101426.01.log"); it is the file
            // the watcher is on, not anything out of it.
            _redactor.Redact(snapshot.ActiveFile is null ? null : Path.GetFileName(snapshot.ActiveFile)),
            snapshot.Offset,
            snapshot.SinceUtc,
            snapshot.LastLineUtc,
            _eventStore.Count,
            _eventStore.LastTimestamp,
            BuildEventTypeCounts());
    }

    /// <summary>
    /// How many of each event the session saw. Names and counts only - the events themselves carry
    /// the commander's systems, stations, ship ids and balances, and none of that is read here.
    /// </summary>
    private IReadOnlyDictionary<string, int> BuildEventTypeCounts() =>
        _eventStore.GetRecent(JournalEventStore.MaxEvents)
            .GroupBy(journalEvent => string.IsNullOrWhiteSpace(journalEvent.Event)
                ? "[unnamed event]"
                : _redactor.Redact(journalEvent.Event)!)
            .Select(group => (Name: group.Key, Count: group.Count()))
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .Take(MaxEventTypeEntries)
            .ToDictionary(entry => entry.Name, entry => entry.Count);

    private IReadOnlyList<DiagnosticsHealthIndicator> BuildHealth() =>
        _health.GetReport().Components
            .Select(indicator => new DiagnosticsHealthIndicator(
                indicator.Id,
                indicator.Name,
                indicator.Status,
                _redactor.Redact(indicator.Reason) ?? string.Empty,
                _redactor.Redact(indicator.Detail)))
            .ToList();

    private IReadOnlyList<RecordedError> BuildRecentErrors() =>
        _recentErrors.GetRecent(RecentErrorLog.MaxErrors)
            .Select(error => error with
            {
                Message = _redactor.Redact(error.Message) ?? string.Empty
            })
            .ToList();

    /// <summary>
    /// Whether the configured journal folder is there. A path this process cannot even stat must not
    /// turn bundle creation into a failure - that folder is frequently the thing being reported.
    /// </summary>
    private bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check the configured journal folder for the diagnostics bundle");
            return false;
        }
    }
}
