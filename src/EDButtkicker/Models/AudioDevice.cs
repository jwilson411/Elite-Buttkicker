namespace EDButtkicker.Models;

public class AudioDevice
{
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