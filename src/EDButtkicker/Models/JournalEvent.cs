using System.Text.Json.Serialization;

namespace EDButtkicker.Models;

public class JournalEvent
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }
    
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;
    
    [JsonPropertyName("StarSystem")]
    public string? StarSystem { get; set; }
    
    [JsonPropertyName("SystemAddress")]
    public long? SystemAddress { get; set; }
    
    [JsonPropertyName("StarPos")]
    public double[]? StarPos { get; set; }
    
    [JsonPropertyName("Docked")]
    public bool? Docked { get; set; }
    
    [JsonPropertyName("StationName")]
    public string? StationName { get; set; }
    
    [JsonPropertyName("Health")]
    public double? Health { get; set; }
    
    [JsonPropertyName("HullDamage")]
    public double? HullDamage { get; set; }
    
    [JsonPropertyName("ShipID")]
    public long? ShipID { get; set; }
    
    [JsonPropertyName("Ship")]
    public string? Ship { get; set; }
    
    [JsonPropertyName("Target")]
    public string? Target { get; set; }
    
    [JsonPropertyName("TargetLocked")]
    public bool? TargetLocked { get; set; }
    
    [JsonExtensionData]
    public Dictionary<string, object>? AdditionalData { get; set; }
}