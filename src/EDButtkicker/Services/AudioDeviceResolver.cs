using EDButtkicker.Models;

namespace EDButtkicker.Services;

/// <summary>What a saved audio selection turned out to mean against a live device list.</summary>
public enum AudioDeviceResolutionStatus
{
    /// <summary>Nothing was saved (or the system default entry was saved): use the default output.</summary>
    SystemDefault,

    /// <summary>Exactly one connected, active device is the saved one.</summary>
    Resolved,

    /// <summary>The saved device is not in this enumeration - unplugged, disabled or renamed.</summary>
    Unresolved,

    /// <summary>Several devices answer to the saved name and nothing distinguishes them.</summary>
    Ambiguous,

    /// <summary>The saved device is present but not in an active state, so it cannot be opened.</summary>
    Unavailable
}

/// <summary>
/// The outcome of resolving saved settings against an enumeration. Only <see cref="IsUsable"/>
/// means "open this device"; every other status means playback has to fall back to the system
/// default output, and <see cref="Reason"/> says which case it was in words a log can carry.
/// </summary>
public sealed record AudioDeviceResolution(
    AudioDeviceResolutionStatus Status,
    AudioDevice? Device,
    string Reason)
{
    /// <summary>The resolved device can be opened by endpoint id.</summary>
    public bool IsUsable => Status == AudioDeviceResolutionStatus.Resolved;

    /// <summary>Playback should use the default output rather than a named endpoint.</summary>
    public bool UsesSystemDefault => Status != AudioDeviceResolutionStatus.Resolved;

    /// <summary>Nothing was saved at all, as opposed to a saved device that could not be honoured.</summary>
    public bool IsSystemDefault => Status == AudioDeviceResolutionStatus.SystemDefault;

    public string EndpointId => Device?.EndpointId ?? string.Empty;

    public string Name => Device?.Name ?? string.Empty;

    /// <summary>True when the resolved device is also the endpoint Windows reports as the default.</summary>
    public bool IsDefaultEndpoint => Device?.IsDefault ?? Status == AudioDeviceResolutionStatus.SystemDefault;
}

/// <summary>
/// Turns saved audio settings into one device out of an enumeration. The identity is the MMDevice
/// endpoint id: it survives devices being reordered, unplugged and plugged back in, and it tells
/// two outputs with the same friendly name apart. The list ordinal (DeviceId) is not identity -
/// it moves whenever the endpoint list changes - so it is only ever used to disambiguate settings
/// written before endpoint ids were persisted, and never to pick a neighbouring device.
///
/// Deliberately free of NAudio types: callers map their enumeration into <see cref="AudioDevice"/>
/// first, so every resolution rule is exercisable without an audio device.
/// </summary>
public static class AudioDeviceResolver
{
    public static AudioDeviceResolution Resolve(
        IReadOnlyList<AudioDevice>? devices,
        string? endpointId,
        string? name,
        int deviceId = WasapiAudioDeviceCatalog.SystemDefaultDeviceId)
    {
        var enumerated = devices ?? Array.Empty<AudioDevice>();
        var savedEndpointId = (endpointId ?? string.Empty).Trim();
        var savedName = (name ?? string.Empty).Trim();

        if (savedEndpointId.Length > 0)
        {
            return ResolveByEndpointId(enumerated, savedEndpointId, savedName);
        }

        if (savedName.Length == 0)
        {
            return new AudioDeviceResolution(
                AudioDeviceResolutionStatus.SystemDefault,
                null,
                $"configured: no endpoint id and no device name saved, playback uses the system default output " +
                $"(saved DeviceId={deviceId} does not identify a device)");
        }

        return ResolveByName(enumerated, savedName, deviceId);
    }

    private static AudioDeviceResolution ResolveByEndpointId(
        IReadOnlyList<AudioDevice> enumerated,
        string savedEndpointId,
        string savedName)
    {
        var match = enumerated.FirstOrDefault(d =>
            string.Equals(d.EndpointId, savedEndpointId, StringComparison.Ordinal));

        if (match == null)
        {
            return new AudioDeviceResolution(
                AudioDeviceResolutionStatus.Unresolved,
                null,
                $"configured: UNRESOLVED - endpoint id '{savedEndpointId}'{DescribeSavedName(savedName)} is not in this " +
                $"enumeration; playback falls back to the system default");
        }

        if (!match.IsAvailable)
        {
            return new AudioDeviceResolution(
                AudioDeviceResolutionStatus.Unavailable,
                match,
                $"configured: UNAVAILABLE - endpoint id '{savedEndpointId}' is name '{match.Name}' but is not active; " +
                $"playback falls back to the system default");
        }

        var renamed = savedName.Length > 0 && !string.Equals(savedName, match.Name, StringComparison.Ordinal)
            ? $", saved under the name '{savedName}'"
            : string.Empty;

        return new AudioDeviceResolution(
            AudioDeviceResolutionStatus.Resolved,
            match,
            $"configured: endpoint id '{savedEndpointId}' is name '{match.Name}' at position " +
            $"{PositionOf(enumerated, match)} of {enumerated.Count}{renamed}");
    }

    private static AudioDeviceResolution ResolveByName(
        IReadOnlyList<AudioDevice> enumerated,
        string savedName,
        int deviceId)
    {
        // Ordinal equality, matching how MMDevice.FriendlyName is compared everywhere else.
        var matches = enumerated
            .Where(d => string.Equals(d.Name, savedName, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0)
        {
            return new AudioDeviceResolution(
                AudioDeviceResolutionStatus.Unresolved,
                null,
                $"configured: UNRESOLVED - name '{savedName}' is not in this enumeration " +
                $"(saved DeviceId={deviceId} is an ordinal, not an identity); playback falls back to the system default");
        }

        if (matches.Count == 1)
        {
            return DescribeSingleNameMatch(enumerated, matches[0], savedName);
        }

        // Settings written before endpoint ids were persisted carry only a name and a list ordinal.
        // The ordinal is accepted here only when it still lands on a device of that same name; it is
        // never allowed to select a differently named neighbour.
        var legacy = matches.Where(d => d.DeviceId == deviceId).ToList();
        if (legacy.Count == 1)
        {
            var match = legacy[0];
            if (!match.IsAvailable)
            {
                return new AudioDeviceResolution(
                    AudioDeviceResolutionStatus.Unavailable,
                    match,
                    $"configured: UNAVAILABLE - name '{savedName}' at saved DeviceId={deviceId} is not active; " +
                    $"playback falls back to the system default");
            }

            return new AudioDeviceResolution(
                AudioDeviceResolutionStatus.Resolved,
                match,
                $"configured: {matches.Count} devices share name '{savedName}'; the saved DeviceId={deviceId} still " +
                $"names one of them (endpoint id '{match.EndpointId}'), so it is used - re-select this device to save its endpoint id");
        }

        var ids = string.Join(", ", matches.Select(m => $"DeviceId={m.DeviceId}"));
        return new AudioDeviceResolution(
            AudioDeviceResolutionStatus.Ambiguous,
            null,
            $"configured: AMBIGUOUS - {matches.Count} devices share name '{savedName}' ({ids}) and no endpoint id is " +
            $"saved; playback falls back to the system default");
    }

    private static AudioDeviceResolution DescribeSingleNameMatch(
        IReadOnlyList<AudioDevice> enumerated,
        AudioDevice match,
        string savedName)
    {
        // The synthetic "system default" entry is a choice, not an endpoint: it has no id to open.
        if (match.EndpointId.Length == 0 && match.DeviceId == WasapiAudioDeviceCatalog.SystemDefaultDeviceId)
        {
            return new AudioDeviceResolution(
                AudioDeviceResolutionStatus.SystemDefault,
                null,
                $"configured: name '{savedName}' is the system default entry, playback uses the system default output");
        }

        if (!match.IsAvailable)
        {
            return new AudioDeviceResolution(
                AudioDeviceResolutionStatus.Unavailable,
                match,
                $"configured: UNAVAILABLE - name '{savedName}' is present but not active; " +
                $"playback falls back to the system default");
        }

        return new AudioDeviceResolution(
            AudioDeviceResolutionStatus.Resolved,
            match,
            $"configured: name '{savedName}' resolves to endpoint id '{match.EndpointId}' at position " +
            $"{PositionOf(enumerated, match)} of {enumerated.Count}; no endpoint id was saved, so it was matched by name");
    }

    private static int PositionOf(IReadOnlyList<AudioDevice> enumerated, AudioDevice device)
    {
        for (var i = 0; i < enumerated.Count; i++)
        {
            if (ReferenceEquals(enumerated[i], device)) return i + 1;
        }

        return 0;
    }

    private static string DescribeSavedName(string savedName) =>
        savedName.Length == 0 ? string.Empty : $" (name '{savedName}')";
}
