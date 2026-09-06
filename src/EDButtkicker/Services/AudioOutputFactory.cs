using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EDButtkicker.Services;

/// <summary>
/// One opened output, together with what it actually turned out to be. The backend and the device
/// are recorded by whoever opened it, because the fallback path can land on a different backend and
/// a different endpoint than the saved selection asked for.
/// </summary>
public sealed record AudioOutputHandle(IWavePlayer Player, string Backend, string? EndpointId, string? DeviceName);

/// <summary>
/// Opening an audio output. Behind an interface because every implementation of it talks to real
/// hardware through WASAPI or WinMM: the fallback rules, the failure handling and the effect
/// lifetime all have to be exercisable on a machine (and a CI agent) with no output device at all.
/// </summary>
public interface IAudioOutputFactory
{
    /// <summary>
    /// Opens one WASAPI render endpoint by its endpoint id. Throws when the endpoint cannot be
    /// opened, which is the caller's signal to fall back to <see cref="OpenDefault"/>.
    /// </summary>
    AudioOutputHandle OpenEndpoint(string endpointId);

    /// <summary>Opens whatever the OS currently considers the default output.</summary>
    AudioOutputHandle OpenDefault();
}

/// <summary>
/// The production adapter: WASAPI for a named endpoint, WinMM for the default output. Deliberately
/// thin - it constructs NAudio objects and names what it opened, and makes no fallback decisions of
/// its own, so all of those live in <see cref="AudioEngineService"/> where they can be tested.
/// </summary>
public sealed class NAudioOutputFactory : IAudioOutputFactory
{
    /// <summary>Shared-mode WASAPI latency, in milliseconds.</summary>
    private const int WasapiLatencyMs = 200;

    private readonly ILogger<NAudioOutputFactory> _logger;

    public NAudioOutputFactory(ILogger<NAudioOutputFactory> logger)
    {
        _logger = logger;
    }

    public AudioOutputHandle OpenEndpoint(string endpointId)
    {
        var enumerator = new MMDeviceEnumerator();
        var endpoint = enumerator.GetDevice(endpointId);
        var player = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, WasapiLatencyMs);

        return new AudioOutputHandle(player, "WASAPI", endpointId, endpoint.FriendlyName);
    }

    public AudioOutputHandle OpenDefault() =>
        new(new WaveOutEvent(), "WaveOut", null, TryGetDefaultDeviceName());

    /// <summary>The default device's name is only for the log and the health payload, so a failure
    /// to read it must not stop the output from opening.</summary>
    private string? TryGetDefaultDeviceName()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).FriendlyName;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to get default audio device name: {Error}", ex.Message);
            return null;
        }
    }
}
