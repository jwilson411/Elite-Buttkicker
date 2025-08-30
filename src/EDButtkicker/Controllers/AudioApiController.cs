using Microsoft.AspNetCore.Http;
using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace EDButtkicker.Controllers;

public class AudioApiController
{
    private readonly ILogger<AudioApiController> _logger;
    private readonly AppSettings _settings;
    private readonly AudioEngineService _audioEngine;

    public AudioApiController(
        ILogger<AudioApiController> logger, 
        AppSettings settings,
        AudioEngineService audioEngine)
    {
        _logger = logger;
        _settings = settings;
        _audioEngine = audioEngine;
    }

    public async Task GetAudioDevices(HttpContext context)
    {
        try
        {
            var devices = GetAvailableAudioDevices();
            
            var response = new
            {
                devices = devices.Select(d => new
                {
                    id = d.DeviceId,
                    name = d.Name,
                    driver = d.Driver,
                    channels = d.Channels,
                    isDefault = d.IsDefault,
                    isAvailable = d.IsAvailable
                }),
                current = new
                {
                    id = _settings.Audio.AudioDeviceId,
                    name = _settings.Audio.AudioDeviceName
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

            var deviceData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (deviceData == null || !deviceData.ContainsKey("deviceId"))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Device ID is required" }));
                return;
            }

            var deviceIdString = deviceData["deviceId"].ToString();
            if (!int.TryParse(deviceIdString, out int deviceId))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Invalid device ID format" }));
                return;
            }

            var devices = GetAvailableAudioDevices();
            var selectedDevice = devices.FirstOrDefault(d => d.DeviceId == deviceId);
            
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

            // Update settings
            _settings.Audio.AudioDeviceId = selectedDevice.DeviceId;
            _settings.Audio.AudioDeviceName = selectedDevice.Name;

            _logger.LogInformation("Audio device changed to: {DeviceName} (ID: {DeviceId})", 
                selectedDevice.Name, selectedDevice.DeviceId);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new 
            { 
                success = true, 
                message = "Audio device updated successfully",
                device = new
                {
                    id = selectedDevice.DeviceId,
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

    private List<AudioDevice> GetAvailableAudioDevices()
    {
        var devices = new List<AudioDevice>();

        try
        {
            // Use MMDevice enumerator for better device detection
            var deviceEnumerator = new MMDeviceEnumerator();
            var devicesCollection = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            
            for (int i = 0; i < devicesCollection.Count; i++)
            {
                var device = devicesCollection[i];
                devices.Add(new AudioDevice
                {
                    DeviceId = i,
                    Name = device.FriendlyName,
                    Driver = "WASAPI",
                    Channels = 2, // Default assumption
                    IsDefault = device.ID == deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID,
                    IsAvailable = true
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error enumerating audio devices, using fallback");
            
            // Fallback - add a default device entry
            devices.Add(new AudioDevice
            {
                DeviceId = -1,
                Name = "Default Audio Device",
                Driver = "Default",
                Channels = 2,
                IsDefault = true,
                IsAvailable = true
            });
        }

        return devices;
    }
}