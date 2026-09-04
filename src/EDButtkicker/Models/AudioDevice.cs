namespace EDButtkicker.Models;

public class AudioDevice
{
    /// <summary>
    /// The MMDevice endpoint id, which is this device's identity: it is stable across reordering,
    /// unplugging and replugging, and it distinguishes two outputs that share a friendly name.
    /// Empty for the synthetic "system default" entry, which is a choice rather than an endpoint.
    /// </summary>
    public string EndpointId { get; set; } = string.Empty;

    /// <summary>
    /// Position in the enumeration this device came from (-1 for the system default entry). A
    /// display and compatibility coordinate only - it moves when devices come and go, so it must
    /// never be used to identify a device.
    /// </summary>
    public int DeviceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Driver { get; set; } = string.Empty;
    public int Channels { get; set; }
    public bool IsDefault { get; set; }
    public bool IsAvailable { get; set; }
    
    public override string ToString()
    {
        return $"{Name} ({Driver}) - {Channels} channels{(IsDefault ? " [Default]" : "")}";
    }
}