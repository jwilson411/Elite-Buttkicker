using EDButtkicker.Configuration;
using Microsoft.Extensions.Logging;

namespace EDButtkicker.Services;

/// <summary>
/// A requested settings change. Every field is optional and null means "leave this one alone", so a
/// caller that only knows about one setting can never blank out the rest of the configuration.
/// The audio device name and endpoint id are deliberately nullable strings rather than empty-string
/// sentinels: an empty value is a real choice - it is how "use the system default" is recorded.
/// </summary>
public sealed class SettingsUpdate
{
    public int? AudioDeviceId { get; set; }
    public string? AudioDeviceEndpointId { get; set; }
    public string? AudioDeviceName { get; set; }
    public int? MaxIntensity { get; set; }
    public int? DefaultFrequency { get; set; }
    public int? SampleRate { get; set; }
    public int? BufferSize { get; set; }

    public string? JournalPath { get; set; }
    public bool? MonitorLatestOnly { get; set; }

    public bool? ContextualIntelligenceEnabled { get; set; }
    public double? LearningRate { get; set; }
    public double? PredictionThreshold { get; set; }
    public bool? EnableAdaptiveIntensity { get; set; }
    public bool? EnablePredictivePatterns { get; set; }
    public bool? EnableContextualVoice { get; set; }
    public bool? LogContextAnalysis { get; set; }
}

/// <summary>
/// One setting that actually changed, and whether the running application is already using it.
/// <paramref name="AppliedNow"/> is the honest answer to "do I have to restart?" - it is false only
/// when the subsystem that reads the setting cannot pick it up without a restart.
/// </summary>
public sealed record SettingsChange(string Setting, string Value, bool AppliedNow, string Detail);

/// <summary>
/// What a settings mutation did: whether it was accepted, what changed, whether it reached disk, and
/// which of the changes the running process is already honouring.
/// </summary>
public sealed class SettingsUpdateResult
{
    private SettingsUpdateResult(
        bool valid,
        IReadOnlyList<string> validationErrors,
        IReadOnlyList<SettingsChange> changes,
        bool saved,
        string? saveError,
        string settingsPath)
    {
        Valid = valid;
        ValidationErrors = validationErrors;
        Changes = changes;
        Saved = saved;
        SaveError = saveError;
        SettingsPath = settingsPath;
    }

    /// <summary>False when nothing was applied and nothing was written: the request was rejected.</summary>
    public bool Valid { get; }

    public IReadOnlyList<string> ValidationErrors { get; }

    public IReadOnlyList<SettingsChange> Changes { get; }

    /// <summary>True once the settings file on disk reflects the change.</summary>
    public bool Saved { get; }

    public string? SaveError { get; }

    public string SettingsPath { get; }

    public bool RestartRequired => Changes.Any(c => !c.AppliedNow);

    public IReadOnlyList<string> RestartRequiredSettings =>
        Changes.Where(c => !c.AppliedNow).Select(c => c.Setting).ToList();

    /// <summary>How much of this change the running process is honouring right now.</summary>
    public string AppliedState => Changes.Count == 0
        ? "no_changes"
        : Changes.All(c => c.AppliedNow)
            ? "immediately"
            : Changes.Any(c => c.AppliedNow)
                ? "partly"
                : "after_restart";

    /// <summary>
    /// One sentence a user can act on. It never says "saved" unless the file was written, and never
    /// implies a change is live when the subsystem only reads it at startup.
    /// </summary>
    public string Message
    {
        get
        {
            if (!Valid)
            {
                return $"No settings were changed: {string.Join(" ", ValidationErrors)}";
            }

            if (Changes.Count == 0)
            {
                return "No settings changed.";
            }

            // Neither the settings file's location nor the exception that stopped the write belongs
            // in a response body; both are already in the log line next to the failure.
            var where = Saved
                ? "Saved to the settings file."
                : "NOT saved to the settings file - these values apply to this session only and will be gone after a restart.";

            var live = Changes.Where(c => c.AppliedNow).Select(c => c.Setting).ToList();
            var pending = RestartRequiredSettings;

            return pending.Count == 0
                ? $"{where} In effect now: {string.Join(", ", live)}."
                : live.Count == 0
                    ? $"{where} Restart the application to apply: {string.Join(", ", pending)}."
                    : $"{where} In effect now: {string.Join(", ", live)}. " +
                      $"Restart the application to apply: {string.Join(", ", pending)}.";
        }
    }

    /// <summary>
    /// The block every settings mutation returns, so a caller never has to guess whether a 200 means
    /// "live", "written" or merely "accepted".
    /// </summary>
    public object ToPayload() => new
    {
        saved = Saved,
        applied = AppliedState,
        restart_required = RestartRequired,
        restart_required_settings = RestartRequiredSettings,
        changes = Changes.Select(c => new
        {
            setting = c.Setting,
            value = c.Value,
            applied_now = c.AppliedNow,
            detail = c.Detail
        }).ToList(),
        message = Message
    };

    internal static SettingsUpdateResult Rejected(IReadOnlyList<string> errors, string settingsPath) =>
        new(false, errors, Array.Empty<SettingsChange>(), saved: false, saveError: null, settingsPath);

    internal static SettingsUpdateResult Applied(
        IReadOnlyList<SettingsChange> changes,
        bool saved,
        string? saveError,
        string settingsPath) =>
        new(true, Array.Empty<string>(), changes, saved, saveError, settingsPath);
}

/// <summary>
/// The single place a settings change is validated, applied to the running configuration, handed to
/// the subsystems that can take it live, and written to disk. Every settings route goes through here
/// so that a successful response means the same thing everywhere: the value was checked, the file on
/// disk holds it, and the response says plainly whether the process is already using it.
/// </summary>
public class SettingsPersistenceService
{
    /// <summary>Intensity is a percentage of the configured maximum output.</summary>
    public const int MinIntensity = 1;
    public const int MaxIntensity = 100;

    /// <summary>A buttkicker reproduces low frequencies; anything outside this is a typo, not a choice.</summary>
    public const int MinFrequency = 10;
    public const int MaxFrequency = 200;

    public const int MinSampleRate = 8000;
    public const int MaxSampleRate = 192000;

    public const int MinBufferSize = 64;
    public const int MaxBufferSize = 16384;

    private const string RestartAudioFormatDetail =
        "Saved. The audio output keeps the format it was opened with, so this takes effect the next time the application starts.";

    private const string RestartJournalModeDetail =
        "Saved. The journal reader keeps the mode it attached with, so this takes effect the next time the application starts.";

    private readonly ILogger<SettingsPersistenceService> _logger;
    private readonly AppSettings _settings;
    private readonly UserSettingsService _userSettings;
    private readonly AudioEngineService _audioEngine;
    private readonly JournalMonitorStatus _journalStatus;

    // One writer at a time: two overlapping requests must not interleave a mutation with a write and
    // leave the file describing a configuration that never existed.
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SettingsPersistenceService(
        ILogger<SettingsPersistenceService> logger,
        AppSettings settings,
        UserSettingsService userSettings,
        AudioEngineService audioEngine,
        JournalMonitorStatus journalStatus)
    {
        _logger = logger;
        _settings = settings;
        _userSettings = userSettings;
        _audioEngine = audioEngine;
        _journalStatus = journalStatus;
    }

    public string SettingsPath => _userSettings.GetUserSettingsPath();

    /// <summary>
    /// Validates <paramref name="update"/>, applies what actually differs, takes live what can be
    /// taken live, and persists the result. A rejected update changes nothing at all - neither the
    /// running configuration nor the file - so an invalid value can never destroy a working one.
    /// </summary>
    public async Task<SettingsUpdateResult> ApplyAsync(SettingsUpdate update)
    {
        var errors = Validate(update);
        if (errors.Count > 0)
        {
            _logger.LogWarning("Rejected a settings change: {Errors}", string.Join(" ", errors));
            return SettingsUpdateResult.Rejected(errors, SettingsPath);
        }

        await _mutex.WaitAsync();
        try
        {
            var changes = ApplyToRunningConfiguration(update);

            if (changes.Count == 0)
            {
                return SettingsUpdateResult.Applied(changes, saved: true, saveError: null, SettingsPath);
            }

            try
            {
                await _userSettings.SaveUserPreferencesAsync(
                    _userSettings.CreatePreferencesFromAppSettings(_settings));

                _logger.LogInformation(
                    "Persisted settings change: {Changes}",
                    string.Join(", ", changes.Select(c => $"{c.Setting}={c.Value}")));

                return SettingsUpdateResult.Applied(changes, saved: true, saveError: null, SettingsPath);
            }
            catch (Exception ex)
            {
                // The values are live for this session; only the record of them is missing, and the
                // caller has to be able to say so rather than report a save that did not happen.
                _logger.LogError(ex, "Settings change applied to this session but could not be saved to {SettingsPath}", SettingsPath);
                return SettingsUpdateResult.Applied(changes, saved: false, saveError: ex.Message, SettingsPath);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Writes the running configuration exactly as it stands. This is for callers that have already
    /// validated and applied a change themselves - the first-run wizard - so that they still write
    /// through the one atomic, backed-up path instead of a second one of their own.
    /// </summary>
    public async Task<bool> PersistCurrentAsync()
    {
        await _mutex.WaitAsync();
        try
        {
            await _userSettings.SaveUserPreferencesAsync(
                _userSettings.CreatePreferencesFromAppSettings(_settings));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist the running configuration to {SettingsPath}", SettingsPath);
            return false;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Every reason this update cannot be accepted, checked before anything is mutated. Journal paths
    /// are expanded first, so a user typing %USERPROFILE% is validated on the folder they meant.
    /// </summary>
    public static IReadOnlyList<string> Validate(SettingsUpdate update)
    {
        var errors = new List<string>();

        if (update.MaxIntensity is { } intensity && (intensity < MinIntensity || intensity > MaxIntensity))
        {
            errors.Add($"Maximum intensity must be between {MinIntensity} and {MaxIntensity} (got {intensity}).");
        }

        if (update.DefaultFrequency is { } frequency && (frequency < MinFrequency || frequency > MaxFrequency))
        {
            errors.Add($"Default frequency must be between {MinFrequency} and {MaxFrequency} Hz (got {frequency}).");
        }

        if (update.SampleRate is { } sampleRate && (sampleRate < MinSampleRate || sampleRate > MaxSampleRate))
        {
            errors.Add($"Sample rate must be between {MinSampleRate} and {MaxSampleRate} Hz (got {sampleRate}).");
        }

        if (update.BufferSize is { } bufferSize && (bufferSize < MinBufferSize || bufferSize > MaxBufferSize))
        {
            errors.Add($"Buffer size must be between {MinBufferSize} and {MaxBufferSize} samples (got {bufferSize}).");
        }

        if (update.AudioDeviceId is { } deviceId && deviceId < WasapiAudioDeviceCatalog.SystemDefaultDeviceId)
        {
            errors.Add($"Audio device id must be {WasapiAudioDeviceCatalog.SystemDefaultDeviceId} (the system default) or greater (got {deviceId}).");
        }

        if (update.JournalPath != null)
        {
            var journalPath = ExpandJournalPath(update.JournalPath);

            if (string.IsNullOrWhiteSpace(journalPath))
            {
                errors.Add("The journal folder path cannot be empty.");
            }
            else if (!Directory.Exists(journalPath))
            {
                errors.Add($"The journal folder does not exist: {journalPath}");
            }
        }

        if (update.LearningRate is { } learningRate && (learningRate < 0.01 || learningRate > 1.0))
        {
            errors.Add($"Learning rate must be between 0.01 and 1.0 (got {learningRate}).");
        }

        if (update.PredictionThreshold is { } threshold && (threshold < 0.1 || threshold > 1.0))
        {
            errors.Add($"Prediction threshold must be between 0.1 and 1.0 (got {threshold}).");
        }

        return errors;
    }

    /// <summary>The path as the user meant it: %USERPROFILE% expanded, surrounding blanks removed.</summary>
    public static string ExpandJournalPath(string path) =>
        JournalPathDiscovery.ExpandUserProfile(path.Trim());

    /// <summary>
    /// Applies the fields that actually differ from the running configuration and reports each one,
    /// including whether the subsystem behind it took the new value live.
    /// </summary>
    private List<SettingsChange> ApplyToRunningConfiguration(SettingsUpdate update)
    {
        var changes = new List<SettingsChange>();

        // The output device is three fields describing one choice, so they are applied together and
        // reported against a single reinitialisation of the audio engine.
        var deviceFields = new List<(string Setting, string Value)>();

        if (update.AudioDeviceEndpointId != null &&
            update.AudioDeviceEndpointId != _settings.Audio.AudioDeviceEndpointId)
        {
            _settings.Audio.AudioDeviceEndpointId = update.AudioDeviceEndpointId;
            deviceFields.Add(("audio.audioDeviceEndpointId", update.AudioDeviceEndpointId));
        }

        if (update.AudioDeviceName != null && update.AudioDeviceName != _settings.Audio.AudioDeviceName)
        {
            _settings.Audio.AudioDeviceName = update.AudioDeviceName;
            deviceFields.Add(("audio.audioDeviceName", update.AudioDeviceName));
        }

        if (update.AudioDeviceId is { } deviceId && deviceId != _settings.Audio.AudioDeviceId)
        {
            _settings.Audio.AudioDeviceId = deviceId;
            deviceFields.Add(("audio.audioDeviceId", deviceId.ToString()));
        }

        if (deviceFields.Count > 0)
        {
            var (appliedNow, detail) = ReopenAudioOutput();

            foreach (var (setting, value) in deviceFields)
            {
                changes.Add(new SettingsChange(setting, value, appliedNow, detail));
            }
        }

        if (update.MaxIntensity is { } maxIntensity && maxIntensity != _settings.Audio.MaxIntensity)
        {
            _settings.Audio.MaxIntensity = maxIntensity;
            changes.Add(new SettingsChange(
                "audio.maxIntensity", maxIntensity.ToString(), true,
                "In effect now: every pattern is scaled against this maximum when it plays."));
        }

        if (update.DefaultFrequency is { } defaultFrequency && defaultFrequency != _settings.Audio.DefaultFrequency)
        {
            _settings.Audio.DefaultFrequency = defaultFrequency;
            changes.Add(new SettingsChange(
                "audio.defaultFrequency", defaultFrequency.ToString(), true,
                "In effect now: patterns without their own frequency use this one."));
        }

        if (update.SampleRate is { } sampleRate && sampleRate != _settings.Audio.SampleRate)
        {
            _settings.Audio.SampleRate = sampleRate;
            changes.Add(new SettingsChange(
                "audio.sampleRate", sampleRate.ToString(), false, RestartAudioFormatDetail));
        }

        if (update.BufferSize is { } bufferSize && bufferSize != _settings.Audio.BufferSize)
        {
            _settings.Audio.BufferSize = bufferSize;
            changes.Add(new SettingsChange(
                "audio.bufferSize", bufferSize.ToString(), false, RestartAudioFormatDetail));
        }

        if (update.JournalPath != null)
        {
            var journalPath = ExpandJournalPath(update.JournalPath);

            if (journalPath != _settings.EliteDangerous.JournalPath)
            {
                _settings.EliteDangerous.JournalPath = journalPath;

                // The watcher is still attached to the old folder, so ask it to look again now
                // rather than at the end of its next sweep.
                _journalStatus.RequestRecheck();

                changes.Add(new SettingsChange(
                    "eliteDangerous.journalPath", journalPath, true,
                    "In effect now: the journal watcher was asked to attach to this folder."));
            }
        }

        if (update.MonitorLatestOnly is { } monitorLatestOnly &&
            monitorLatestOnly != _settings.EliteDangerous.MonitorLatestOnly)
        {
            _settings.EliteDangerous.MonitorLatestOnly = monitorLatestOnly;
            changes.Add(new SettingsChange(
                "eliteDangerous.monitorLatestOnly", monitorLatestOnly.ToString(), false, RestartJournalModeDetail));
        }

        changes.AddRange(ApplyContextualIntelligence(update));

        return changes;
    }

    /// <summary>
    /// Contextual intelligence is read from the settings object on every event, so each of these is
    /// live the moment it is assigned.
    /// </summary>
    private List<SettingsChange> ApplyContextualIntelligence(SettingsUpdate update)
    {
        var changes = new List<SettingsChange>();

        if (update.ContextualIntelligenceEnabled == null &&
            update.LearningRate == null &&
            update.PredictionThreshold == null &&
            update.EnableAdaptiveIntensity == null &&
            update.EnablePredictivePatterns == null &&
            update.EnableContextualVoice == null &&
            update.LogContextAnalysis == null)
        {
            return changes;
        }

        _settings.ContextualIntelligence ??= new ContextualIntelligenceConfiguration();
        var config = _settings.ContextualIntelligence;

        const string liveDetail = "In effect now: contextual intelligence reads this setting on every event.";

        void Record(string setting, string value) =>
            changes.Add(new SettingsChange(setting, value, true, liveDetail));

        if (update.ContextualIntelligenceEnabled is { } enabled && enabled != config.Enabled)
        {
            config.Enabled = enabled;
            Record("contextualIntelligence.enabled", enabled.ToString());
        }

        if (update.LearningRate is { } learningRate && Math.Abs(learningRate - config.LearningRate) > double.Epsilon)
        {
            config.LearningRate = learningRate;
            Record("contextualIntelligence.learningRate", learningRate.ToString("0.###"));
        }

        if (update.PredictionThreshold is { } threshold && Math.Abs(threshold - config.PredictionThreshold) > double.Epsilon)
        {
            config.PredictionThreshold = threshold;
            Record("contextualIntelligence.predictionThreshold", threshold.ToString("0.###"));
        }

        if (update.EnableAdaptiveIntensity is { } adaptive && adaptive != config.EnableAdaptiveIntensity)
        {
            config.EnableAdaptiveIntensity = adaptive;
            Record("contextualIntelligence.enableAdaptiveIntensity", adaptive.ToString());
        }

        if (update.EnablePredictivePatterns is { } predictive && predictive != config.EnablePredictivePatterns)
        {
            config.EnablePredictivePatterns = predictive;
            Record("contextualIntelligence.enablePredictivePatterns", predictive.ToString());
        }

        if (update.EnableContextualVoice is { } voice && voice != config.EnableContextualVoice)
        {
            config.EnableContextualVoice = voice;
            Record("contextualIntelligence.enableContextualVoice", voice.ToString());
        }

        if (update.LogContextAnalysis is { } logAnalysis && logAnalysis != config.LogContextAnalysis)
        {
            config.LogContextAnalysis = logAnalysis;
            Record("contextualIntelligence.logContextAnalysis", logAnalysis.ToString());
        }

        return changes;
    }

    /// <summary>
    /// Drops the currently open output so the next pattern opens the newly selected one. Reported
    /// honestly: if the engine refuses to let go of the old device, the change is on disk but the
    /// running process is still on the previous output until it restarts.
    /// </summary>
    private (bool AppliedNow, string Detail) ReopenAudioOutput()
    {
        try
        {
            _audioEngine.Reinitialize();

            return (true,
                "In effect now: the audio output was released, so the next pattern opens this device.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not reopen the audio output after a device change");

            return (false,
                "Saved, but the audio output could not be reopened, so this device is used " +
                "the next time the application starts.");
        }
    }
}
