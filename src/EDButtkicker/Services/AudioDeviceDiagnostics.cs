using EDButtkicker.Models;

namespace EDButtkicker.Services;

/// <summary>
/// Turns an enumerated device list into log lines that name both coordinates of every device:
/// the 1-based position it occupies in the list a human sees, and the 0-based DeviceId that gets
/// stored in settings. Those two differ by one for WASAPI render endpoints, and differ by more
/// than one wherever a synthetic "Default" entry is prepended, which is what makes "the device
/// names are off by one" reports impossible to confirm from the old logs.
///
/// Deliberately free of NAudio types: callers map their enumeration into <see cref="AudioDevice"/>
/// first, so the formatting is exercisable without an audio device.
/// </summary>
public static class AudioDeviceDiagnostics
{
    /// <summary>
    /// One line per device, each stating position, DeviceId and name together. <paramref name="ordinalSpace"/>
    /// labels which enumeration the positions and ids belong to (WASAPI render endpoints, the web
    /// device list, ...) because ids from one space do not address devices in another.
    /// </summary>
    public static IReadOnlyList<string> DescribeEnumeration(IReadOnlyList<AudioDevice>? devices, string ordinalSpace)
    {
        if (devices == null || devices.Count == 0)
        {
            return new[] { $"{ordinalSpace}: no devices enumerated" };
        }

        var lines = new List<string>(devices.Count);
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            lines.Add(
                $"{ordinalSpace} position {i + 1} of {devices.Count}: " +
                $"DeviceId={device.DeviceId} endpoint='{device.EndpointId}' name='{device.Name}' " +
                $"default={YesNo(device.IsDefault)} available={YesNo(device.IsAvailable)}");
        }

        return lines;
    }

    /// <summary>
    /// Describes what the saved settings actually resolve to. AudioEngineService opens the device
    /// whose friendly name matches <paramref name="configuredDeviceName"/> exactly, so the saved
    /// DeviceId is only ever a label; when the two disagree this line says so instead of leaving
    /// the reader to guess which one playback followed.
    /// </summary>
    public static string DescribeConfiguredSelection(
        IReadOnlyList<AudioDevice>? devices,
        int configuredDeviceId,
        string? configuredDeviceName)
    {
        var enumerated = devices ?? Array.Empty<AudioDevice>();

        if (string.IsNullOrEmpty(configuredDeviceName))
        {
            return $"configured: no device name saved, playback uses the system default output " +
                   $"(saved DeviceId={configuredDeviceId} does not pick the device)";
        }

        // Ordinal equality, matching how the engine matches MMDevice.FriendlyName.
        var matches = Enumerable.Range(0, enumerated.Count)
            .Where(i => string.Equals(enumerated[i].Name, configuredDeviceName, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0)
        {
            return $"configured: UNRESOLVED - name '{configuredDeviceName}' is not in this enumeration " +
                   $"({DescribeSavedId(enumerated, configuredDeviceId)}); playback falls back to the system default";
        }

        if (matches.Count > 1)
        {
            var ids = string.Join(", ", matches.Select(i => $"DeviceId={enumerated[i].DeviceId}"));
            return $"configured: AMBIGUOUS - {matches.Count} devices share name '{configuredDeviceName}' ({ids}); " +
                   $"playback uses the first, saved DeviceId={configuredDeviceId}";
        }

        var position = matches[0] + 1;
        var match = enumerated[matches[0]];
        var located = $"name '{configuredDeviceName}' resolves to DeviceId={match.DeviceId} " +
                      $"at position {position} of {enumerated.Count}";

        if (match.DeviceId == configuredDeviceId)
        {
            return $"configured: {located}, which matches saved DeviceId={configuredDeviceId}";
        }

        return $"configured: MISMATCH - {located}, but {DescribeSavedId(enumerated, configuredDeviceId)}; " +
               $"playback follows the name";
    }

    private static string DescribeSavedId(IReadOnlyList<AudioDevice> devices, int configuredDeviceId)
    {
        var byId = devices.FirstOrDefault(d => d.DeviceId == configuredDeviceId);
        return byId == null
            ? $"saved DeviceId={configuredDeviceId} is not in this enumeration"
            : $"saved DeviceId={configuredDeviceId} is name '{byId.Name}'";
    }

    private static string YesNo(bool value) => value ? "yes" : "no";
}
