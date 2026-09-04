using EDButtkicker.Models;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace EDButtkicker.Services;

/// <summary>
/// The output devices the app can honestly offer. Behind an interface because enumeration talks to
/// WASAPI: the health checks, the setup wizard and the audio API all need a device list on machines
/// (and CI agents) where that call cannot succeed at all.
/// </summary>
public interface IAudioDeviceCatalog
{
    /// <summary>The system default entry (id -1) followed by every active render endpoint.</summary>
    IReadOnlyList<AudioDevice> GetDevices();
}

/// <summary>
/// WASAPI-backed catalog. When enumeration fails the list is not padded with invented entries -
/// the caller gets the system default and nothing else, which is exactly what is usable.
/// </summary>
public class WasapiAudioDeviceCatalog : IAudioDeviceCatalog
{
    public const int SystemDefaultDeviceId = -1;
    public const string SystemDefaultDeviceName = "Default Audio Device";

    private readonly ILogger<WasapiAudioDeviceCatalog> _logger;

    public WasapiAudioDeviceCatalog(ILogger<WasapiAudioDeviceCatalog> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<AudioDevice> GetDevices()
    {
        var devices = new List<AudioDevice>
        {
            new()
            {
                DeviceId = SystemDefaultDeviceId,
                Name = SystemDefaultDeviceName,
                Driver = "Default",
                Channels = 2,
                IsDefault = true,
                IsAvailable = true
            }
        };

        try
        {
            var enumerator = new MMDeviceEnumerator();
            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var defaultEndpointId = TryGetDefaultEndpointId(enumerator);

            for (int i = 0; i < endpoints.Count; i++)
            {
                var endpoint = endpoints[i];

                devices.Add(new AudioDevice
                {
                    // The endpoint id is the identity; the index is only where it happens to sit today.
                    EndpointId = endpoint.ID,
                    DeviceId = i,
                    Name = endpoint.FriendlyName,
                    Driver = "WASAPI",
                    Channels = 2,
                    IsDefault = defaultEndpointId != null && endpoint.ID == defaultEndpointId,
                    IsAvailable = endpoint.State == DeviceState.Active
                });
            }
        }
        catch (Exception ex)
        {
            // No WASAPI here (no hardware, or not Windows): the system default is all we can offer.
            _logger.LogWarning(ex, "Error enumerating audio devices, offering the system default only");
        }

        return devices;
    }

    private string? TryGetDefaultEndpointId(MMDeviceEnumerator enumerator)
    {
        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No default render endpoint reported");
            return null;
        }
    }
}
