using EDButtkicker.Models;
using EDButtkicker.Services;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The saved output selection has to mean the same device tomorrow as it did today. These pin the
/// rules that make that true: the endpoint id is the identity, the DeviceId is an ordinal that may
/// not be used to pick a neighbour, and anything that cannot be honoured has to say so rather than
/// quietly open something else. All of it runs against constructed device lists, so no audio
/// hardware - and no WASAPI - is involved.
/// </summary>
public class AudioDeviceResolverTests
{
    private const string SpeakersEndpoint = "{0.0.0.00000000}.{aaaaaaaa-1111-2222-3333-444444444444}";
    private const string ButtkickerEndpoint = "{0.0.0.00000000}.{bbbbbbbb-1111-2222-3333-444444444444}";
    private const string HeadphonesEndpoint = "{0.0.0.00000000}.{cccccccc-1111-2222-3333-444444444444}";

    private static AudioDevice Device(
        string endpointId,
        int deviceId,
        string name,
        bool isDefault = false,
        bool isAvailable = true) =>
        new()
        {
            EndpointId = endpointId,
            DeviceId = deviceId,
            Name = name,
            Driver = "WASAPI",
            Channels = 2,
            IsDefault = isDefault,
            IsAvailable = isAvailable
        };

    private static AudioDevice SystemDefaultEntry() =>
        new()
        {
            DeviceId = WasapiAudioDeviceCatalog.SystemDefaultDeviceId,
            Name = WasapiAudioDeviceCatalog.SystemDefaultDeviceName,
            Driver = "Default",
            Channels = 2,
            IsDefault = true,
            IsAvailable = true
        };

    private static List<AudioDevice> Enumeration() => new()
    {
        Device(SpeakersEndpoint, 0, "Speakers (Realtek)"),
        Device(ButtkickerEndpoint, 1, "Buttkicker (USB Audio)", isDefault: true),
        Device(HeadphonesEndpoint, 2, "Headphones (USB Audio)")
    };

    [Fact]
    public void SavedEndpointId_StillResolvesAfterTheDevicesAreReordered()
    {
        // Same endpoints, different order: every DeviceId has moved, so a resolver that trusted
        // the ordinal would now open the speakers.
        var reordered = new List<AudioDevice>
        {
            Device(HeadphonesEndpoint, 0, "Headphones (USB Audio)"),
            Device(ButtkickerEndpoint, 1, "Buttkicker (USB Audio)", isDefault: true),
            Device(SpeakersEndpoint, 2, "Speakers (Realtek)")
        };

        var resolution = AudioDeviceResolver.Resolve(
            reordered, ButtkickerEndpoint, "Buttkicker (USB Audio)", deviceId: 2);

        Assert.Equal(AudioDeviceResolutionStatus.Resolved, resolution.Status);
        Assert.True(resolution.IsUsable);
        Assert.Equal(ButtkickerEndpoint, resolution.EndpointId);
        Assert.Equal("Buttkicker (USB Audio)", resolution.Name);
    }

    [Fact]
    public void SavedEndpointIdThatIsGone_IsUnresolvedRatherThanTheNeighbourAtThatDeviceId()
    {
        var withoutButtkicker = new List<AudioDevice>
        {
            Device(SpeakersEndpoint, 0, "Speakers (Realtek)"),
            Device(HeadphonesEndpoint, 1, "Headphones (USB Audio)")
        };

        var resolution = AudioDeviceResolver.Resolve(
            withoutButtkicker, ButtkickerEndpoint, "Buttkicker (USB Audio)", deviceId: 1);

        Assert.Equal(AudioDeviceResolutionStatus.Unresolved, resolution.Status);
        Assert.False(resolution.IsUsable);
        Assert.True(resolution.UsesSystemDefault);
        Assert.False(resolution.IsSystemDefault);
        Assert.Null(resolution.Device);
        Assert.Contains("UNRESOLVED", resolution.Reason);
    }

    [Fact]
    public void SavedEndpointIdThatIsPresentButNotActive_IsUnavailable()
    {
        var disabled = new List<AudioDevice>
        {
            Device(SpeakersEndpoint, 0, "Speakers (Realtek)"),
            Device(ButtkickerEndpoint, 1, "Buttkicker (USB Audio)", isAvailable: false)
        };

        var resolution = AudioDeviceResolver.Resolve(
            disabled, ButtkickerEndpoint, "Buttkicker (USB Audio)", deviceId: 1);

        Assert.Equal(AudioDeviceResolutionStatus.Unavailable, resolution.Status);
        Assert.False(resolution.IsUsable);
        Assert.True(resolution.UsesSystemDefault);
        // The device is still named, because "your buttkicker is disabled" is the useful report.
        Assert.Equal("Buttkicker (USB Audio)", resolution.Device!.Name);
        Assert.Contains("UNAVAILABLE", resolution.Reason);
    }

    [Fact]
    public void DuplicateNames_AreToldApartByTheSavedEndpointId()
    {
        var duplicates = new List<AudioDevice>
        {
            Device(SpeakersEndpoint, 0, "USB Audio Device"),
            Device(ButtkickerEndpoint, 1, "USB Audio Device"),
            Device(HeadphonesEndpoint, 2, "USB Audio Device")
        };

        var resolution = AudioDeviceResolver.Resolve(duplicates, ButtkickerEndpoint, "USB Audio Device", deviceId: 0);

        Assert.Equal(AudioDeviceResolutionStatus.Resolved, resolution.Status);
        Assert.Equal(ButtkickerEndpoint, resolution.EndpointId);
    }

    [Fact]
    public void DuplicateNamesWithNoEndpointIdSaved_AreAmbiguousWhenTheOrdinalNamesNoneOfThem()
    {
        var duplicates = new List<AudioDevice>
        {
            Device(SpeakersEndpoint, 0, "USB Audio Device"),
            Device(ButtkickerEndpoint, 1, "Realtek Speakers"),
            Device(HeadphonesEndpoint, 2, "USB Audio Device")
        };

        var resolution = AudioDeviceResolver.Resolve(duplicates, endpointId: null, name: "USB Audio Device", deviceId: 1);

        Assert.Equal(AudioDeviceResolutionStatus.Ambiguous, resolution.Status);
        Assert.Null(resolution.Device);
        Assert.True(resolution.UsesSystemDefault);
        Assert.Contains("AMBIGUOUS", resolution.Reason);
    }

    [Fact]
    public void DuplicateNamesWithNoEndpointIdSaved_AcceptTheOrdinalOnlyWhenItNamesOneOfThem()
    {
        // Legacy settings carry a name and an ordinal. The ordinal is allowed to break the tie
        // between two devices of that same name, and nothing more.
        var duplicates = new List<AudioDevice>
        {
            Device(SpeakersEndpoint, 0, "USB Audio Device"),
            Device(ButtkickerEndpoint, 1, "Realtek Speakers"),
            Device(HeadphonesEndpoint, 2, "USB Audio Device")
        };

        var resolution = AudioDeviceResolver.Resolve(duplicates, endpointId: "", name: "USB Audio Device", deviceId: 2);

        Assert.Equal(AudioDeviceResolutionStatus.Resolved, resolution.Status);
        Assert.Equal(HeadphonesEndpoint, resolution.EndpointId);
    }

    [Fact]
    public void ANewlyConnectedDevice_DoesNotStealTheSavedSelection()
    {
        // A device plugged in ahead of the saved one pushes every later DeviceId along by one.
        var withNewDevice = new List<AudioDevice>
        {
            Device("{0.0.0.00000000}.{dddddddd-0000-0000-0000-000000000000}", 0, "New Interface"),
            Device(SpeakersEndpoint, 1, "Speakers (Realtek)"),
            Device(ButtkickerEndpoint, 2, "Buttkicker (USB Audio)"),
            Device(HeadphonesEndpoint, 3, "Headphones (USB Audio)")
        };

        var resolution = AudioDeviceResolver.Resolve(
            withNewDevice, ButtkickerEndpoint, "Buttkicker (USB Audio)", deviceId: 1);

        Assert.Equal(AudioDeviceResolutionStatus.Resolved, resolution.Status);
        Assert.Equal(ButtkickerEndpoint, resolution.EndpointId);
        Assert.Equal("Buttkicker (USB Audio)", resolution.Name);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "  ")]
    public void NothingSaved_IsTheSystemDefault(string? endpointId, string? name)
    {
        var resolution = AudioDeviceResolver.Resolve(Enumeration(), endpointId, name);

        Assert.Equal(AudioDeviceResolutionStatus.SystemDefault, resolution.Status);
        Assert.True(resolution.IsSystemDefault);
        Assert.True(resolution.UsesSystemDefault);
        Assert.False(resolution.IsUsable);
        Assert.Null(resolution.Device);
        Assert.Equal(string.Empty, resolution.EndpointId);
    }

    [Fact]
    public void AUniqueNameWithNoEndpointIdSaved_StillResolves()
    {
        // Settings written before endpoint ids were persisted must keep working.
        var resolution = AudioDeviceResolver.Resolve(
            Enumeration(), endpointId: null, name: "Buttkicker (USB Audio)", deviceId: 1);

        Assert.Equal(AudioDeviceResolutionStatus.Resolved, resolution.Status);
        Assert.Equal(ButtkickerEndpoint, resolution.EndpointId);
        Assert.Contains("matched by name", resolution.Reason);
    }

    [Fact]
    public void AUniqueNameThatIsNoLongerActive_IsUnavailable()
    {
        var disabled = new List<AudioDevice>
        {
            Device(ButtkickerEndpoint, 0, "Buttkicker (USB Audio)", isAvailable: false)
        };

        var resolution = AudioDeviceResolver.Resolve(
            disabled, endpointId: null, name: "Buttkicker (USB Audio)", deviceId: 0);

        Assert.Equal(AudioDeviceResolutionStatus.Unavailable, resolution.Status);
        Assert.False(resolution.IsUsable);
    }

    [Fact]
    public void TheSystemDefaultEntry_ResolvesToTheSystemDefaultRatherThanAnEndpoint()
    {
        var webList = new List<AudioDevice> { SystemDefaultEntry() };
        webList.AddRange(Enumeration());

        var resolution = AudioDeviceResolver.Resolve(
            webList,
            endpointId: null,
            name: WasapiAudioDeviceCatalog.SystemDefaultDeviceName,
            deviceId: WasapiAudioDeviceCatalog.SystemDefaultDeviceId);

        Assert.Equal(AudioDeviceResolutionStatus.SystemDefault, resolution.Status);
        Assert.True(resolution.IsSystemDefault);
        Assert.Null(resolution.Device);
    }

    [Fact]
    public void ARenamedDevice_StillResolvesByEndpointIdAndSaysSo()
    {
        var resolution = AudioDeviceResolver.Resolve(
            Enumeration(), ButtkickerEndpoint, "Buttkicker (old name)", deviceId: 1);

        Assert.Equal(AudioDeviceResolutionStatus.Resolved, resolution.Status);
        Assert.Equal("Buttkicker (USB Audio)", resolution.Name);
        Assert.Contains("saved under the name 'Buttkicker (old name)'", resolution.Reason);
    }

    [Fact]
    public void AnEmptyEnumeration_LeavesASavedEndpointUnresolved()
    {
        var resolution = AudioDeviceResolver.Resolve(
            new List<AudioDevice>(), ButtkickerEndpoint, "Buttkicker (USB Audio)", deviceId: 1);

        Assert.Equal(AudioDeviceResolutionStatus.Unresolved, resolution.Status);

        Assert.Equal(
            AudioDeviceResolutionStatus.Unresolved,
            AudioDeviceResolver.Resolve(null, ButtkickerEndpoint, "Buttkicker (USB Audio)").Status);
    }

    [Fact]
    public void EndpointIdsAreMatchedExactly()
    {
        // MMDevice ids are opaque strings; a case-insensitive match could pick a different endpoint.
        var resolution = AudioDeviceResolver.Resolve(
            Enumeration(), ButtkickerEndpoint.ToUpperInvariant(), "Buttkicker (USB Audio)", deviceId: 1);

        Assert.Equal(AudioDeviceResolutionStatus.Unresolved, resolution.Status);
    }
}
