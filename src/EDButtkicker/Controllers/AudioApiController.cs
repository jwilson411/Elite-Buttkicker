using Microsoft.AspNetCore.Http;
using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Hosting;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging;

namespace EDButtkicker.Controllers;

public class AudioApiController
{
    private readonly ILogger<AudioApiController> _logger;
    private readonly AppSettings _settings;
    private readonly AudioEngineService _audioEngine;
    private readonly IAudioDeviceCatalog _deviceCatalog;
    private readonly SettingsPersistenceService _settingsPersistence;

    public AudioApiController(
        ILogger<AudioApiController> logger,
        AppSettings settings,
        AudioEngineService audioEngine,
        IAudioDeviceCatalog deviceCatalog,
        SettingsPersistenceService settingsPersistence)
    {
        _logger = logger;
        _settings = settings;
        _audioEngine = audioEngine;
        _deviceCatalog = deviceCatalog;
        _settingsPersistence = settingsPersistence;
    }

    public async Task GetAudioDevices(HttpContext context)
    {
        try
        {
            var devices = GetAvailableAudioDevices();

            // This list prepends a synthetic default entry, so a device's position in the web list is
            // never its DeviceId. Log both so a mis-selection report can be read against the ids.
            foreach (var line in AudioDeviceDiagnostics.DescribeEnumeration(devices, "web device list"))
            {
                _logger.LogInformation("Audio devices - {Device}", line);
            }

            var resolution = ResolveConfiguredDevice(devices);
            _logger.LogInformation("Audio devices - {Selection}", resolution.Reason);

            var response = new
            {
                devices = devices.Select(d => new
                {
                    id = d.DeviceId,
                    endpointId = d.EndpointId,
                    name = d.Name,
                    driver = d.Driver,
                    channels = d.Channels,
                    isDefault = d.IsDefault,
                    isAvailable = d.IsAvailable,
                    // Which row the web UI highlights: the resolved endpoint, or the system default
                    // entry when nothing is saved or the saved device is gone.
                    isSelected = IsSelected(d, resolution)
                }),
                current = new
                {
                    id = _settings.Audio.AudioDeviceId,
                    endpointId = _settings.Audio.AudioDeviceEndpointId,
                    name = _settings.Audio.AudioDeviceName,
                    resolved = resolution.Status.ToString(),
                    resolvedEndpointId = resolution.EndpointId,
                    resolvedName = resolution.Name,
                    usesSystemDefault = resolution.UsesSystemDefault,
                    reason = resolution.Reason
                },
                metadata = new
                {
                    total_devices = devices.Count,
                    available_devices = devices.Count(d => d.IsAvailable)
                }
            };

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(response, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting audio devices");
            await ApiError.WriteAsync(context, 500, "Failed to list audio devices");
        }
    }

    public async Task SetAudioDevice(HttpContext context)
    {
        try
        {
            var json = await BoundedRequestReader.ReadOrRespondAsync(context, "Request body is empty");
            if (json == null)
            {
                return;
            }

            if (!BoundedRequestReader.TryDeserialize<Dictionary<string, JsonElement>>(json, out var deviceData))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Request body is not valid JSON" }));
                return;
            }

            var requestedEndpointId = ReadString(deviceData, "endpointId");
            var requestedDeviceId = ReadInt(deviceData, "deviceId");

            if (string.IsNullOrWhiteSpace(requestedEndpointId) && requestedDeviceId == null)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "An endpoint id or device ID is required" }));
                return;
            }

            var devices = GetAvailableAudioDevices();

            // Endpoint id is the identity, so it wins whenever the caller sends one; the numeric id
            // stays accepted for the existing UI, where it addresses this very list.
            var selectedDevice = !string.IsNullOrWhiteSpace(requestedEndpointId)
                ? devices.FirstOrDefault(d => string.Equals(d.EndpointId, requestedEndpointId, StringComparison.Ordinal))
                : devices.FirstOrDefault(d => d.DeviceId == requestedDeviceId);

            if (selectedDevice == null)
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Device not found" }));
                return;
            }

            if (!selectedDevice.IsAvailable)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Device is not available" }));
                return;
            }

            // The synthetic default entry is a choice, not an endpoint: it is stored as the empty
            // endpoint id and empty name the audio engine already reads as "use the default".
            var isSystemDefault = selectedDevice.DeviceId == WasapiAudioDeviceCatalog.SystemDefaultDeviceId;

            // The selection is applied, taken live (the engine releases the open output so the next
            // pattern opens this one) and written to the settings file by the one persistence
            // service, so the device is still selected after a restart.
            var result = await _settingsPersistence.ApplyAsync(new SettingsUpdate
            {
                AudioDeviceEndpointId = isSystemDefault ? string.Empty : selectedDevice.EndpointId,
                AudioDeviceName = isSystemDefault ? string.Empty : selectedDevice.Name,
                AudioDeviceId = selectedDevice.DeviceId
            });

            if (!result.Valid)
            {
                context.Response.StatusCode = 400;
                await WriteJsonAsync(context, new { error = result.Message, validation_errors = result.ValidationErrors });
                return;
            }

            _logger.LogInformation("Audio device changed to: {DeviceName} (endpoint {EndpointId}, ordinal {DeviceId}): {Message}",
                selectedDevice.Name, _settings.Audio.AudioDeviceEndpointId, selectedDevice.DeviceId, result.Message);

            context.Response.StatusCode = result.Saved ? 200 : 500;
            await WriteJsonAsync(context, new
            {
                success = result.Saved,
                message = result.Message,
                device = new
                {
                    id = selectedDevice.DeviceId,
                    endpointId = selectedDevice.EndpointId,
                    name = selectedDevice.Name,
                    driver = selectedDevice.Driver
                },
                settings = result.ToPayload()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting audio device");
            await ApiError.WriteAsync(context, 500, "Failed to set the audio device");
        }
    }

    /// <summary>
    /// What the output is actually doing: the selected endpoint, the backend carrying audio, whether
    /// a device is open, and why the last playback failed. Reading this never opens a device, so the
    /// UI can show "not opened yet" without provoking hardware.
    /// </summary>
    public async Task GetAudioStatus(HttpContext context)
    {
        try
        {
            await WriteJsonAsync(context, BuildStatusPayload());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading audio status");
            await ApiError.WriteAsync(context, 500, "Failed to read the audio status");
        }
    }

    /// <summary>
    /// Plays the shared, deliberately quiet test tone. The request fails unless the tone actually
    /// reached an open output: a scheduled effect on a device that was never opened is not a
    /// successful test, and reporting it as one is what let a silent rig look healthy.
    /// </summary>
    public async Task TestAudio(HttpContext context)
    {
        try
        {
            _logger.LogInformation("Testing audio output");

            var testPattern = AudioTestPattern.Create(_settings.Audio, "Audio Test");

            // Forget any earlier failure first: a user pressing Test after reconnecting their amp
            // is asking for another attempt, not for the old verdict.
            var opened = _audioEngine.RetryInitialization();
            var playback = opened
                ? await _audioEngine.TryPlayHapticPattern(testPattern)
                : AudioPlaybackResult.Failed(DescribeUnavailableOutput());

            if (!playback.Played)
            {
                _logger.LogWarning("Audio test failed: {Error}", playback.Error);

                // 503 when there is no output to reach, 500 when an open output refused the tone.
                context.Response.StatusCode = opened ? 500 : 503;
                await WriteJsonAsync(context, new
                {
                    success = false,
                    played = false,
                    error = playback.Error,
                    audio = BuildStatusPayload()
                });
                return;
            }

            await WriteJsonAsync(context, new
            {
                success = true,
                played = true,
                message = $"Played a {testPattern.Duration} ms tone at {testPattern.Frequency} Hz and " +
                    $"{testPattern.Intensity}% intensity.",
                pattern = new
                {
                    name = testPattern.Name,
                    frequency = testPattern.Frequency,
                    duration = testPattern.Duration,
                    intensity = testPattern.Intensity
                },
                stop = "/api/audio/stop",
                audio = BuildStatusPayload()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing audio");
            await ApiError.WriteAsync(context, 500, "Failed to test the audio output");
        }
    }

    /// <summary>
    /// Silences everything playing right now. This is the way out of a tone that is too strong, so
    /// it takes effect immediately rather than waiting for the effect's own cleanup.
    /// </summary>
    public async Task StopAudio(HttpContext context)
    {
        try
        {
            var stopped = _audioEngine.StopAllEffects();
            _logger.LogInformation("Stopped {Count} active audio effect(s) on request", stopped);

            await WriteJsonAsync(context, new
            {
                success = true,
                stopped,
                message = stopped == 0
                    ? "Nothing was playing."
                    : $"Stopped {stopped} active effect(s).",
                audio = BuildStatusPayload()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping audio playback");
            await ApiError.WriteAsync(context, 500, "Failed to stop audio playback");
        }
    }

    /// <summary>
    /// The health contract behind every audio response: selection, backend, initialization state and
    /// the last playback error, all read from the engine rather than inferred from a 200.
    /// </summary>
    private object BuildStatusPayload()
    {
        var status = _audioEngine.GetStatus();
        var devices = GetAvailableAudioDevices();
        var resolution = ResolveConfiguredDevice(devices);

        return new
        {
            initialized = status.Initialized,
            initializationFailed = status.InitializationFailed,
            // Null until a device has been opened - naming a backend before then would be a guess.
            backend = status.Backend,
            openedAtUtc = status.OpenedAtUtc,
            lastError = status.LastError,
            lastPlaybackError = status.LastPlaybackError,
            lastPlaybackAtUtc = status.LastPlaybackAtUtc,
            activeEffects = status.ActiveEffects,
            selectedDevice = new
            {
                id = _settings.Audio.AudioDeviceId,
                endpointId = _settings.Audio.AudioDeviceEndpointId,
                name = _settings.Audio.AudioDeviceName,
                resolved = resolution.Status.ToString(),
                resolvedEndpointId = resolution.EndpointId,
                resolvedName = resolution.Name,
                usesSystemDefault = resolution.UsesSystemDefault,
                reason = resolution.Reason
            },
            // What is carrying audio, which is not always what was selected: the engine falls back
            // to the default output when the saved endpoint cannot be opened.
            activeDevice = new
            {
                endpointId = status.ActiveEndpointId,
                name = status.ActiveDeviceName
            },
            test = new
            {
                endpoint = "/api/audio/test",
                stopEndpoint = "/api/audio/stop",
                intensity = AudioTestPattern.IntensityFor(_settings.Audio),
                maxIntensity = _settings.Audio.MaxIntensity,
                durationMs = AudioTestPattern.DurationMs
            }
        };
    }

    private string DescribeUnavailableOutput()
    {
        var status = _audioEngine.GetStatus();

        return string.IsNullOrWhiteSpace(status.LastError)
            ? "No audio output device could be opened, so nothing was played."
            : $"No audio output device could be opened, so nothing was played: {status.LastError}";
    }

    private static Task WriteJsonAsync(HttpContext context, object payload)
    {
        context.Response.ContentType = "application/json";

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    // One enumeration for the whole app: the setup wizard, the health checks and this API all read
    // the same catalog, so they can never disagree about which devices exist.
    private List<AudioDevice> GetAvailableAudioDevices() => _deviceCatalog.GetDevices().ToList();

    private AudioDeviceResolution ResolveConfiguredDevice(IReadOnlyList<AudioDevice> devices) =>
        AudioDeviceResolver.Resolve(
            devices,
            _settings.Audio.AudioDeviceEndpointId,
            _settings.Audio.AudioDeviceName,
            _settings.Audio.AudioDeviceId);

    /// <summary>
    /// The saved selection, by endpoint id where there is one. When nothing resolves, the system
    /// default entry is the honest highlight - that is the output playback will actually use.
    /// </summary>
    private static bool IsSelected(AudioDevice device, AudioDeviceResolution resolution) =>
        resolution.UsesSystemDefault
            ? device.DeviceId == WasapiAudioDeviceCatalog.SystemDefaultDeviceId
            : string.Equals(device.EndpointId, resolution.EndpointId, StringComparison.Ordinal);

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
}