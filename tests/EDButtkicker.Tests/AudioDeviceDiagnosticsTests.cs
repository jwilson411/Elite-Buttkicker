using EDButtkicker.Models;
using EDButtkicker.Services;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The device diagnostic is the only hardware-independent handle on "the device names look off by
/// one" reports, so the shape of every line it emits is pinned here: a position must never be
/// printed without the DeviceId beside it, and a saved name that resolves to a different DeviceId
/// than the saved one must be called out rather than silently reported as the selected device.
/// </summary>
public class AudioDeviceDiagnosticsTests
{
    private static AudioDevice Device(int deviceId, string name, bool isDefault = false, bool isAvailable = true) =>
        new()
        {
            DeviceId = deviceId,
            Name = name,
            Driver = "WASAPI",
            Channels = 2,
            IsDefault = isDefault,
            IsAvailable = isAvailable
        };

    private static List<AudioDevice> WasapiEnumeration() => new()
    {
        Device(0, "Speakers (Realtek)"),
        Device(1, "Buttkicker (USB Audio)", isDefault: true),
        Device(2, "Headphones (USB Audio)")
    };

    [Fact]
    public void DescribeEnumeration_PairsEveryPositionWithItsDeviceId()
    {
        var lines = AudioDeviceDiagnostics.DescribeEnumeration(WasapiEnumeration(), "WASAPI render");

        Assert.Equal(3, lines.Count);
        Assert.Equal(
            "WASAPI render position 1 of 3: DeviceId=0 name='Speakers (Realtek)' default=no available=yes",
            lines[0]);
        Assert.Equal(
            "WASAPI render position 2 of 3: DeviceId=1 name='Buttkicker (USB Audio)' default=yes available=yes",
            lines[1]);
        Assert.Equal(
            "WASAPI render position 3 of 3: DeviceId=2 name='Headphones (USB Audio)' default=no available=yes",
            lines[2]);
    }

    [Fact]
    public void DescribeEnumeration_ReportsPositionsOfAListThatDoesNotStartAtDeviceIdZero()
    {
        // The web device list prepends a synthetic default, so position and DeviceId are skewed by
        // more than one. Both coordinates still have to appear on every line.
        var webList = new List<AudioDevice>
        {
            Device(-1, "Default Audio Device", isDefault: true),
            Device(0, "Speakers (Realtek)"),
            Device(1, "Buttkicker (USB Audio)")
        };

        var lines = AudioDeviceDiagnostics.DescribeEnumeration(webList, "web device list");

        Assert.Equal(
            "web device list position 1 of 3: DeviceId=-1 name='Default Audio Device' default=yes available=yes",
            lines[0]);
        Assert.Equal(
            "web device list position 3 of 3: DeviceId=1 name='Buttkicker (USB Audio)' default=no available=yes",
            lines[2]);
    }

    [Fact]
    public void DescribeEnumeration_MarksUnavailableDevices()
    {
        var lines = AudioDeviceDiagnostics.DescribeEnumeration(
            new List<AudioDevice> { Device(0, "Unplugged", isAvailable: false) },
            "WASAPI render");

        Assert.Equal("WASAPI render position 1 of 1: DeviceId=0 name='Unplugged' default=no available=no", lines[0]);
    }

    [Fact]
    public void DescribeEnumeration_SaysSoWhenTheListIsEmpty()
    {
        var lines = AudioDeviceDiagnostics.DescribeEnumeration(new List<AudioDevice>(), "WASAPI render");

        Assert.Equal(new[] { "WASAPI render: no devices enumerated" }, lines);
    }

    [Fact]
    public void DescribeEnumeration_SaysSoWhenEnumerationFailedAndReturnedNothing()
    {
        var lines = AudioDeviceDiagnostics.DescribeEnumeration(null, "WASAPI render");

        Assert.Equal(new[] { "WASAPI render: no devices enumerated" }, lines);
    }

    [Fact]
    public void DescribeConfiguredSelection_ReportsAgreementBetweenSavedNameAndSavedId()
    {
        var line = AudioDeviceDiagnostics.DescribeConfiguredSelection(
            WasapiEnumeration(), configuredDeviceId: 1, configuredDeviceName: "Buttkicker (USB Audio)");

        Assert.Equal(
            "configured: name 'Buttkicker (USB Audio)' resolves to DeviceId=1 at position 2 of 3, " +
            "which matches saved DeviceId=1",
            line);
    }

    [Fact]
    public void DescribeConfiguredSelection_FlagsOffByOneBetweenSavedNameAndSavedId()
    {
        // The exact shape a "names are off by one" report produces: the saved id is the menu
        // position rather than the DeviceId of the saved name.
        var line = AudioDeviceDiagnostics.DescribeConfiguredSelection(
            WasapiEnumeration(), configuredDeviceId: 2, configuredDeviceName: "Buttkicker (USB Audio)");

        Assert.Equal(
            "configured: MISMATCH - name 'Buttkicker (USB Audio)' resolves to DeviceId=1 at position 2 of 3, " +
            "but saved DeviceId=2 is name 'Headphones (USB Audio)'; playback follows the name",
            line);
    }

    [Fact]
    public void DescribeConfiguredSelection_FlagsMismatchWhenSavedIdIsNotEnumerated()
    {
        var line = AudioDeviceDiagnostics.DescribeConfiguredSelection(
            WasapiEnumeration(), configuredDeviceId: 7, configuredDeviceName: "Speakers (Realtek)");

        Assert.Equal(
            "configured: MISMATCH - name 'Speakers (Realtek)' resolves to DeviceId=0 at position 1 of 3, " +
            "but saved DeviceId=7 is not in this enumeration; playback follows the name",
            line);
    }

    [Fact]
    public void DescribeConfiguredSelection_ReportsANameThatIsNoLongerPresent()
    {
        var line = AudioDeviceDiagnostics.DescribeConfiguredSelection(
            WasapiEnumeration(), configuredDeviceId: 1, configuredDeviceName: "Removed Interface");

        Assert.Equal(
            "configured: UNRESOLVED - name 'Removed Interface' is not in this enumeration " +
            "(saved DeviceId=1 is name 'Buttkicker (USB Audio)'); playback falls back to the system default",
            line);
    }

    [Fact]
    public void DescribeConfiguredSelection_ReportsDuplicateNamesAsAmbiguous()
    {
        var duplicates = new List<AudioDevice>
        {
            Device(0, "USB Audio Device"),
            Device(1, "Speakers (Realtek)"),
            Device(2, "USB Audio Device")
        };

        var line = AudioDeviceDiagnostics.DescribeConfiguredSelection(
            duplicates, configuredDeviceId: 2, configuredDeviceName: "USB Audio Device");

        Assert.Equal(
            "configured: AMBIGUOUS - 2 devices share name 'USB Audio Device' (DeviceId=0, DeviceId=2); " +
            "playback uses the first, saved DeviceId=2",
            line);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DescribeConfiguredSelection_SaysTheSavedIdIsUnusedWhenNoNameIsSaved(string? name)
    {
        // AudioEngineService only ever selects a device by name, so an id saved without a name
        // picks nothing; the diagnostic must not imply otherwise.
        var line = AudioDeviceDiagnostics.DescribeConfiguredSelection(
            WasapiEnumeration(), configuredDeviceId: 1, configuredDeviceName: name);

        Assert.Equal(
            "configured: no device name saved, playback uses the system default output " +
            "(saved DeviceId=1 does not pick the device)",
            line);
    }

    [Fact]
    public void DescribeConfiguredSelection_HandlesAnEmptyEnumeration()
    {
        var line = AudioDeviceDiagnostics.DescribeConfiguredSelection(
            null, configuredDeviceId: 0, configuredDeviceName: "Buttkicker (USB Audio)");

        Assert.Equal(
            "configured: UNRESOLVED - name 'Buttkicker (USB Audio)' is not in this enumeration " +
            "(saved DeviceId=0 is not in this enumeration); playback falls back to the system default",
            line);
    }

    [Fact]
    public void DescribeConfiguredSelection_MatchesNamesCaseSensitively()
    {
        // MMDevice.FriendlyName is matched with ordinal equality by the engine; the diagnostic has
        // to agree with it or it would report a selection the engine never makes.
        var line = AudioDeviceDiagnostics.DescribeConfiguredSelection(
            WasapiEnumeration(), configuredDeviceId: 1, configuredDeviceName: "buttkicker (usb audio)");

        Assert.StartsWith("configured: UNRESOLVED", line);
    }
}
