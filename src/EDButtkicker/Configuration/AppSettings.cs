using EDButtkicker.Services;

namespace EDButtkicker.Configuration;

public class AppSettings
{
    public EliteDangerousSettings EliteDangerous { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public ContextualIntelligenceConfiguration? ContextualIntelligence { get; set; } = new();
}

public class EliteDangerousSettings
{
    public string JournalPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Saved Games", "Frontier Developments", "Elite Dangerous");
    public bool MonitorLatestOnly { get; set; } = true;
}

public class AudioSettings
{
    public int SampleRate { get; set; } = 44100;
    public int BufferSize { get; set; } = 1024;
    public int DefaultFrequency { get; set; } = 40;
    public int MaxIntensity { get; set; } = 80;
    public string AudioDeviceName { get; set; } = string.Empty;
    public int AudioDeviceId { get; set; } = -1; // -1 means use default
}