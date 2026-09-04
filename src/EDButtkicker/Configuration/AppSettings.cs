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
	/// <summary>MMDevice endpoint id of the chosen output; empty means the system default.</summary>
	public string AudioDeviceEndpointId { get; set; } = string.Empty;
	/// <summary>Enumeration ordinal of the chosen output (-1 = system default). Display only.</summary>
	public int AudioDeviceId { get; set; } = -1;
}

public class ContextualIntelligenceConfiguration
{
	public bool Enabled { get; set; } = false;
	public double LearningRate { get; set; } = 0.1;
	public double PredictionThreshold { get; set; } = 0.7;
	public bool EnableAdaptiveIntensity { get; set; } = true;
	public bool EnablePredictivePatterns { get; set; } = true;
	public bool EnableContextualVoice { get; set; } = true;
	public bool LogContextAnalysis { get; set; } = false;
}