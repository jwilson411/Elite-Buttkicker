namespace EDButtkicker.Services;

/// <summary>Build and platform identity of the process that produced a bundle.</summary>
public sealed record DiagnosticsApplicationInfo(
    string Name,
    string Version,
    string Runtime,
    string OperatingSystem,
    string OsArchitecture,
    string ProcessArchitecture);

/// <summary>
/// The settings that decide how the app behaves, with every path reduced to a placeholder. Built by
/// naming each field, not by serialising the settings object: a bundle must not start carrying a new
/// setting's value just because someone added one.
/// </summary>
public sealed record DiagnosticsConfigurationInfo(
    int SampleRate,
    int BufferSize,
    int DefaultFrequency,
    int MaxIntensity,
    string AudioDeviceName,
    string AudioDeviceEndpointId,
    int AudioDeviceId,
    string JournalPath,
    bool JournalPathExists,
    bool MonitorLatestOnly,
    DiagnosticsContextualIntelligenceInfo? ContextualIntelligence,
    DiagnosticsUserSettingsInfo UserSettings);

public sealed record DiagnosticsContextualIntelligenceInfo(
    bool Enabled,
    double LearningRate,
    double PredictionThreshold,
    bool EnableAdaptiveIntensity,
    bool EnablePredictivePatterns,
    bool EnableContextualVoice,
    bool LogContextAnalysis);

/// <summary>
/// Where the saved settings live and whether they were readable, plus the names of any keys in that
/// file this build does not know about. Unknown keys are named and never valued, so a credential
/// someone adds to the file tomorrow cannot ride along in a bundle written by today's build.
/// </summary>
public sealed record DiagnosticsUserSettingsInfo(
    bool Exists,
    string Path,
    DateTime? LastSavedUtc,
    string? Version,
    IReadOnlyList<string> UnknownKeysWithheld);

/// <summary>One output endpoint as the app sees it - the list a hardware report is really about.</summary>
public sealed record DiagnosticsAudioEndpoint(
    int DeviceId,
    string EndpointId,
    string Name,
    string Driver,
    int Channels,
    bool IsDefault,
    bool IsAvailable);

/// <summary>What the audio backend is actually doing, including why opening a device failed.</summary>
public sealed record DiagnosticsAudioBackend(
    bool Initialized,
    bool InitializationFailed,
    string? LastError,
    string? ConfiguredDeviceName,
    string? ActiveDeviceName,
    string? ActiveEndpointId,
    string? Backend,
    DateTime? OpenedAtUtc,
    string? LastPlaybackError,
    DateTime? LastPlaybackAtUtc,
    int ActiveEffects);

/// <summary>
/// The journal watcher's state, and how much it has seen. Event <em>names</em> and counts only: no
/// journal line, and no field out of one, is ever copied in here.
/// </summary>
public sealed record DiagnosticsJournalWatcher(
    string State,
    string Path,
    string Reason,
    string? ActiveFileName,
    long? Offset,
    DateTime? SinceUtc,
    DateTime? LastLineUtc,
    int EventsSeenThisSession,
    DateTime? LastEventUtc,
    IReadOnlyDictionary<string, int> EventTypeCounts);

/// <summary>A health indicator with its text sanitized; the dashboard's own reading, in the bundle.</summary>
public sealed record DiagnosticsHealthIndicator(
    string Id,
    string Name,
    string Status,
    string Reason,
    string? Detail);

/// <summary>
/// The single artifact a user attaches to a GitHub issue. Serialised as one JSON document: it is
/// shown to the user in full before it is written, so it has to stay readable, and a maintainer has
/// to be able to diff two of them.
/// </summary>
public sealed record DiagnosticsBundle(
    int SchemaVersion,
    DateTime GeneratedAtUtc,
    DiagnosticsApplicationInfo Application,
    DiagnosticsConfigurationInfo Configuration,
    IReadOnlyList<DiagnosticsAudioEndpoint> AudioEndpoints,
    DiagnosticsAudioBackend AudioBackend,
    DiagnosticsJournalWatcher JournalWatcher,
    IReadOnlyList<DiagnosticsHealthIndicator> Health,
    IReadOnlyList<RecordedError> RecentErrors,
    /// <summary>
    /// What this bundle deliberately leaves out, carried inside the artifact itself so a maintainer
    /// reading it knows what they must not ask for, and the user can see the promise they were given.
    /// </summary>
    IReadOnlyList<string> ExcludedByDesign);

/// <summary>Outcome of a save attempt: whether a file was written, where, and why not.</summary>
public sealed record DiagnosticsBundleSaveResult(bool Saved, string Path, string Reason);
