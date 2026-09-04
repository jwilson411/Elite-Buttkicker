using Microsoft.AspNetCore.Http;
using System.Text.Json;
using EDButtkicker.Configuration;
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

    public AudioApiController(
        ILogger<AudioApiController> logger,
        AppSettings settings,
        AudioEngineService audioEngine,
        IAudioDeviceCatalog deviceCatalog)
    {
        _logger = logger;
        _settings = settings;
        _audioEngine = audioEngine;
        _deviceCatalog = deviceCatalog;
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
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    public async Task SetAudioDevice(HttpContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var json = await reader.ReadToEndAsync();
            
            if (string.IsNullOrEmpty(json))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Request body is empty" }));
                return;
            }

            var deviceData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
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

            _settings.Audio.AudioDeviceEndpointId = isSystemDefault ? string.Empty : selectedDevice.EndpointId;
            _settings.Audio.AudioDeviceName = isSystemDefault ? string.Empty : selectedDevice.Name;
            _settings.Audio.AudioDeviceId = selectedDevice.DeviceId;

            _logger.LogInformation("Audio device changed to: {DeviceName} (endpoint {EndpointId}, ordinal {DeviceId})",
                selectedDevice.Name, _settings.Audio.AudioDeviceEndpointId, selectedDevice.DeviceId);

            // Reinitialize the audio engine with the new device
            try
            {
                _audioEngine.Reinitialize();
                _logger.LogInformation("Audio engine reinitialized successfully with endpoint {EndpointId}",
                    _settings.Audio.AudioDeviceEndpointId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reinitialize audio engine with new device");
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Failed to initialize new audio device: " + ex.Message }));
                return;
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new 
            { 
                success = true, 
                message = "Audio device updated successfully",
                device = new
                {
                    id = selectedDevice.DeviceId,
                    endpointId = selectedDevice.EndpointId,
                    name = selectedDevice.Name,
                    driver = selectedDevice.Driver
                }
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting audio device");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    public async Task TestAudio(HttpContext context)
    {
        try
        {
            _logger.LogInformation("Testing audio output");

            // Create a test haptic pattern
            var testPattern = new HapticPattern
            {
                Name = "Audio Test",
                Pattern = PatternType.SharpPulse,
                Frequency = 40,
                Duration = 1000,
                Intensity = 60,
                FadeIn = 100,
                FadeOut = 200
            };

            await _audioEngine.PlayHapticPattern(testPattern);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new 
            { 
                success = true, 
                message = "Audio test completed successfully",
                pattern = new
                {
                    name = testPattern.Name,
                    frequency = testPattern.Frequency,
                    duration = testPattern.Duration,
                    intensity = testPattern.Intensity
                },
                device = new
                {
                    id = _settings.Audio.AudioDeviceId,
                    endpointId = _settings.Audio.AudioDeviceEndpointId,
                    name = _settings.Audio.AudioDeviceName
                }
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing audio");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
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