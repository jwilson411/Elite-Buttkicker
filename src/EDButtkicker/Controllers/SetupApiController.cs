using Microsoft.AspNetCore.Http;
using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging;

namespace EDButtkicker.Controllers;

/// <summary>
/// The first-run wizard: journal discovery, a stable output choice, a deliberately quiet audio
/// test, and completion. Every step reports what actually happened - a step is only "done" once the
/// subsystem it configures accepted the value.
/// </summary>
public class SetupApiController
{
    /// <summary>
    /// The test tone is deliberately gentle: a buttkicker is a physical actuator and the user has
    /// not set their amplifier gain yet when they reach this step.
    /// </summary>
    public const int TestIntensityPercent = 30;
    public const int TestDurationMs = 800;
    public const int TestFadeInMs = 200;
    public const int TestFadeOutMs = 300;
    public const int MinTestFrequency = 20;
    public const int MaxTestFrequency = 50;

    private readonly ILogger<SetupApiController> _logger;
    private readonly AppSettings _settings;
    private readonly SetupStateService _setupState;
    private readonly UserSettingsService _userSettings;
    private readonly JournalPathDiscovery _journalDiscovery;
    private readonly JournalMonitorStatus _journalStatus;
    private readonly IAudioDeviceCatalog _deviceCatalog;
    private readonly AudioEngineService _audioEngine;
    private readonly SystemHealthService _health;
    private readonly TimeProvider _timeProvider;

    public SetupApiController(
        ILogger<SetupApiController> logger,
        AppSettings settings,
        SetupStateService setupState,
        UserSettingsService userSettings,
        JournalPathDiscovery journalDiscovery,
        JournalMonitorStatus journalStatus,
        IAudioDeviceCatalog deviceCatalog,
        AudioEngineService audioEngine,
        SystemHealthService health,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _settings = settings;
        _setupState = setupState;
        _userSettings = userSettings;
        _journalDiscovery = journalDiscovery;
        _journalStatus = journalStatus;
        _deviceCatalog = deviceCatalog;
        _audioEngine = audioEngine;
        _health = health;
        _timeProvider = timeProvider;
    }

    public async Task GetStatus(HttpContext context)
    {
        try
        {
            await WriteJsonAsync(context, await BuildStatusAsync());
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(context, ex, "Error building setup status");
        }
    }

    /// <summary>Step 1: the folders the journal could be in, and what is in each of them.</summary>
    public async Task GetJournalCandidates(HttpContext context)
    {
        try
        {
            var candidates = _journalDiscovery.Discover();

            await WriteJsonAsync(context, new
            {
                configured_path = _settings.EliteDangerous.JournalPath,
                candidates = candidates.Select(c => new
                {
                    path = c.Path,
                    source = c.Source,
                    exists = c.Exists,
                    journal_files_found = c.JournalFileCount,
                    latest_journal_write = c.LatestJournalWriteUtc,
                    is_configured = c.IsConfigured,
                    is_recommended = c.IsRecommended
                }).ToList(),
                recommended_path = candidates.FirstOrDefault(c => c.IsRecommended)?.Path
            });
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(context, ex, "Error discovering journal folders");
        }
    }

    /// <summary>Step 1: confirm a journal folder, persist it, and wake the watcher.</summary>
    public async Task ConfirmJournalPath(HttpContext context)
    {
        try
        {
            var body = await ReadJsonAsync(context);
            var requestedPath = ReadString(body, "path");

            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                await WriteBadRequestAsync(context, "A journal folder path is required");
                return;
            }

            var journalPath = JournalPathDiscovery.ExpandUserProfile(requestedPath.Trim());

            if (!Directory.Exists(journalPath))
            {
                await WriteBadRequestAsync(context, "That folder does not exist", new { path = journalPath });
                return;
            }

            var candidate = JournalPathDiscovery.Inspect(journalPath, "confirmed", isConfigured: true);

            _settings.EliteDangerous.JournalPath = journalPath;

            // The watcher may have given up on a folder that did not exist at startup.
            _journalStatus.RequestRecheck();

            if (!await TryPersistUserSettingsAsync())
            {
                // The folder is in use for this session, but it will not come back after a restart,
                // so the step must not be recorded as confirmed.
                await WriteNotSavedAsync(context, "journal folder");
                return;
            }

            var confirmedAt = _timeProvider.GetUtcNow().UtcDateTime;
            await _setupState.UpdateAsync(state =>
            {
                state.JournalConfirmedAtUtc = confirmedAt;
                state.JournalPath = journalPath;
            });

            _logger.LogInformation(
                "Setup confirmed journal path {JournalPath} ({FileCount} journal file(s) present)",
                journalPath, candidate.JournalFileCount);

            await WriteJsonAsync(context, new
            {
                success = true,
                path = journalPath,
                journal_files_found = candidate.JournalFileCount,
                // A brand new install has no journal files yet; that is worth saying, not refusing.
                warning = candidate.JournalFileCount == 0
                    ? "No journal files here yet. This is normal before Elite Dangerous has been run; monitoring starts when the first one appears."
                    : null,
                setup = await BuildStatusAsync()
            });
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(context, ex, "Error confirming journal path");
        }
    }

    /// <summary>Step 2: choose the output. The device name is what gets persisted, because the
    /// enumeration index moves when devices are plugged in or removed.</summary>
    public async Task SelectAudioDevice(HttpContext context)
    {
        try
        {
            var body = await ReadJsonAsync(context);
            var requestedName = ReadString(body, "name");
            var requestedId = ReadInt(body, "deviceId");

            if (string.IsNullOrWhiteSpace(requestedName) && requestedId == null)
            {
                await WriteBadRequestAsync(context, "An output device name or id is required");
                return;
            }

            var devices = _deviceCatalog.GetDevices();
            var device = !string.IsNullOrWhiteSpace(requestedName)
                ? devices.FirstOrDefault(d => string.Equals(d.Name, requestedName, StringComparison.OrdinalIgnoreCase))
                : devices.FirstOrDefault(d => d.DeviceId == requestedId);

            if (device == null)
            {
                await WriteBadRequestAsync(context, "That output device is not connected", new
                {
                    requested_name = requestedName,
                    requested_id = requestedId,
                    available = devices.Select(d => d.Name).ToList()
                });
                return;
            }

            if (!device.IsAvailable)
            {
                await WriteBadRequestAsync(context, "That output device is not currently active", new { name = device.Name });
                return;
            }

            // The system default entry has no stable name to match on later, so it is stored as the
            // empty name the audio engine already reads as "use the default".
            var isSystemDefault = device.DeviceId == WasapiAudioDeviceCatalog.SystemDefaultDeviceId;

            _settings.Audio.AudioDeviceId = device.DeviceId;
            _settings.Audio.AudioDeviceName = isSystemDefault ? string.Empty : device.Name;

            if (!await TryPersistUserSettingsAsync())
            {
                // Same rule as the journal step: an output that will not survive a restart is not a
                // confirmed step, and the previously recorded state stays as it was.
                await WriteNotSavedAsync(context, "output device");
                return;
            }

            var confirmedAt = _timeProvider.GetUtcNow().UtcDateTime;
            await _setupState.UpdateAsync(state =>
            {
                state.AudioDeviceConfirmedAtUtc = confirmedAt;
                state.AudioDeviceName = isSystemDefault ? null : device.Name;
                state.AudioDeviceId = device.DeviceId;

                // A different output invalidates the previous test result.
                state.AudioTestedAtUtc = null;
                state.AudioTestPlayed = null;
                state.AudioTestReason = null;
            });

            _logger.LogInformation("Setup selected audio output {DeviceName} (id {DeviceId})", device.Name, device.DeviceId);

            await WriteJsonAsync(context, new
            {
                success = true,
                device = new
                {
                    id = device.DeviceId,
                    name = device.Name,
                    driver = device.Driver,
                    is_default = device.IsDefault
                },
                setup = await BuildStatusAsync()
            });
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(context, ex, "Error selecting the audio output device");
        }
    }

    /// <summary>Step 3: a short, quiet test tone, reported by what the audio engine actually did.</summary>
    public async Task RunAudioTest(HttpContext context)
    {
        try
        {
            var pattern = BuildTestPattern();
            var opened = _audioEngine.RetryInitialization();

            if (opened)
            {
                await _audioEngine.PlayHapticPattern(pattern);
            }

            var status = _audioEngine.GetStatus();
            var reason = opened
                ? $"Played a {pattern.Duration} ms tone at {pattern.Frequency} Hz and {pattern.Intensity}% intensity."
                : string.IsNullOrWhiteSpace(status.LastError)
                    ? "No audio output device could be opened, so nothing was played."
                    : $"No audio output device could be opened, so nothing was played: {status.LastError}";

            var testedAt = _timeProvider.GetUtcNow().UtcDateTime;
            await _setupState.UpdateAsync(state =>
            {
                state.AudioTestedAtUtc = testedAt;
                state.AudioTestPlayed = opened;
                state.AudioTestReason = reason;
            });

            _logger.LogInformation("Setup audio test ran, played: {Played}", opened);

            await WriteJsonAsync(context, new
            {
                // Truthfully false when no device could be opened - the request succeeded, the
                // playback did not.
                played = opened,
                reason,
                pattern = new
                {
                    name = pattern.Name,
                    frequency = pattern.Frequency,
                    duration = pattern.Duration,
                    intensity = pattern.Intensity
                },
                device = new
                {
                    id = _settings.Audio.AudioDeviceId,
                    name = string.IsNullOrWhiteSpace(_settings.Audio.AudioDeviceName)
                        ? WasapiAudioDeviceCatalog.SystemDefaultDeviceName
                        : _settings.Audio.AudioDeviceName
                },
                setup = await BuildStatusAsync()
            });
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(context, ex, "Error running the setup audio test");
        }
    }

    /// <summary>Step 4: record completion. Skipped steps are listed rather than hidden.</summary>
    public async Task CompleteSetup(HttpContext context)
    {
        try
        {
            var completedAt = _timeProvider.GetUtcNow().UtcDateTime;
            await _setupState.MarkCompletedAsync(completedAt);

            var status = await BuildStatusAsync();
            _logger.LogInformation("First-run setup marked complete at {CompletedAt:u}", completedAt);

            await WriteJsonAsync(context, new
            {
                success = true,
                completed_at = completedAt,
                setup = status
            });
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(context, ex, "Error completing setup");
        }
    }

    /// <summary>Reopens the wizard without discarding the record that it was completed.</summary>
    public async Task ReopenSetup(HttpContext context)
    {
        try
        {
            _setupState.RequestReopen();

            await WriteJsonAsync(context, new
            {
                success = true,
                setup = await BuildStatusAsync()
            });
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(context, ex, "Error reopening setup");
        }
    }

    private HapticPattern BuildTestPattern() => new()
    {
        Name = "Setup Audio Test",
        Pattern = PatternType.SustainedRumble,
        Frequency = Math.Clamp(_settings.Audio.DefaultFrequency, MinTestFrequency, MaxTestFrequency),
        Duration = TestDurationMs,
        Intensity = Math.Min(TestIntensityPercent, Math.Max(1, _settings.Audio.MaxIntensity)),
        FadeIn = TestFadeInMs,
        FadeOut = TestFadeOutMs
    };

    private async Task<object> BuildStatusAsync()
    {
        var state = await _setupState.LoadAsync();
        var journalDone = state.JournalConfirmedAtUtc != null;
        var deviceDone = state.AudioDeviceConfirmedAtUtc != null;
        var testDone = state.AudioTestedAtUtc != null;

        var steps = new[]
        {
            new
            {
                id = "journal",
                title = "Find your Elite Dangerous journal",
                complete = journalDone,
                summary = journalDone
                    ? $"Journal folder: {state.JournalPath}"
                    : "Pick the folder Elite Dangerous writes its journal files to."
            },
            new
            {
                id = "audio-device",
                title = "Choose your output device",
                complete = deviceDone,
                summary = deviceDone
                    ? $"Output: {state.AudioDeviceName ?? WasapiAudioDeviceCatalog.SystemDefaultDeviceName}"
                    : "Pick the device your buttkicker amplifier is connected to."
            },
            new
            {
                id = "audio-test",
                title = "Run a quiet test",
                complete = testDone,
                summary = testDone
                    ? state.AudioTestReason ?? "Audio test ran."
                    : $"Plays a short {TestIntensityPercent}% tone so you can set your amplifier gain safely."
            },
            new
            {
                id = "finish",
                title = "Finish setup",
                complete = state.Completed,
                summary = state.Completed
                    ? $"Setup completed {state.CompletedAtUtc:yyyy-MM-dd HH:mm} UTC. You can reopen this wizard at any time."
                    : "Save this configuration and open the dashboard."
            }
        };

        var incomplete = steps.Where(s => !s.complete).Select(s => s.id).ToList();

        return new
        {
            completed = state.Completed,
            completed_at = state.CompletedAtUtc,
            reopen_requested = _setupState.ReopenRequested,
            // Persisted completion keeps the wizard closed on later runs; a reopen request or an
            // unfinished setup opens it again.
            show_wizard = !state.Completed || _setupState.ReopenRequested,
            current_step = incomplete.FirstOrDefault() ?? "finish",
            incomplete_steps = incomplete,
            steps,
            health = HealthApiController.Serialize(_health.GetReport())
        };
    }

    /// <summary>
    /// Writes the running configuration to the user settings file. Returns false when it could not
    /// be written: the caller must not then record the step as confirmed, because the choice would
    /// be gone on the next run while the wizard claimed it was done.
    /// </summary>
    private async Task<bool> TryPersistUserSettingsAsync()
    {
        try
        {
            await _userSettings.SaveUserPreferencesAsync(_userSettings.CreatePreferencesFromAppSettings(_settings));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Setup could not persist user settings to {SettingsPath}", _userSettings.GetUserSettingsPath());
            return false;
        }
    }

    /// <summary>
    /// Reports a choice that applied to this session but could not be saved, leaving the recorded
    /// setup state exactly as it was.
    /// </summary>
    private async Task WriteNotSavedAsync(HttpContext context, string what)
    {
        var setup = await BuildStatusAsync();

        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = $"The {what} is in use for this session but could not be saved to {_userSettings.GetUserSettingsPath()}, " +
                "so this step is not marked complete. Check that the file is writable and try again.",
            saved = false,
            setup
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<Dictionary<string, JsonElement>?> ReadJsonAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var json = await reader.ReadToEndAsync();

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
    }

    private static string? ReadString(Dictionary<string, JsonElement>? body, string key) =>
        body != null && body.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(Dictionary<string, JsonElement>? body, string key)
    {
        if (body == null || !body.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static Task WriteJsonAsync(HttpContext context, object payload)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static Task WriteBadRequestAsync(HttpContext context, string error, object? detail = null)
    {
        context.Response.StatusCode = 400;
        context.Response.ContentType = "application/json";

        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error,
            detail
        }));
    }

    private async Task WriteErrorAsync(HttpContext context, Exception ex, string message)
    {
        _logger.LogError(ex, "{Message}", message);
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
    }
}
