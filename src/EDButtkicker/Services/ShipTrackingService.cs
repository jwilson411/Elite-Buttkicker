using Microsoft.Extensions.Logging;
using EDButtkicker.Models;
using System.Text.Json;

namespace EDButtkicker.Services;

public class ShipTrackingService
{
    private readonly ILogger<ShipTrackingService> _logger;
    private CurrentShip? _currentShip;
    
    public event Action<CurrentShip>? ShipChanged;
    
    public ShipTrackingService(ILogger<ShipTrackingService> logger)
    {
        _logger = logger;
    }

    public CurrentShip? GetCurrentShip() => _currentShip;

    public void ProcessJournalEvent(JournalEvent journalEvent)
    {
        try
        {
            var previousShip = _currentShip;
            bool shipChanged = false;

            switch (journalEvent.Event)
            {
                case "LoadGame":
                    shipChanged = ProcessLoadGameEvent(journalEvent);
                    break;
                    
                case "ShipyardSwap":
                    shipChanged = ProcessShipyardSwapEvent(journalEvent);
                    break;
                    
                case "Loadout":
                    shipChanged = ProcessLoadoutEvent(journalEvent);
                    break;
                    
                case "StoredShips":
                    ProcessStoredShipsEvent(journalEvent);
                    break;

                // Also track ship from any event that has Ship/ShipID data
                default:
                    if (!string.IsNullOrEmpty(journalEvent.Ship) || journalEvent.ShipID.HasValue)
                    {
                        shipChanged = UpdateCurrentShipFromEvent(journalEvent);
                    }
                    break;
            }

            if (shipChanged && _currentShip != null)
            {
                _logger.LogInformation("Ship changed from {PreviousShip} to {CurrentShip} ({ShipType})", 
                    previousShip?.ShipName ?? "Unknown", 
                    _currentShip.ShipName, 
                    _currentShip.ShipType);
                    
                ShipChanged?.Invoke(_currentShip);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing journal event for ship tracking: {Event}", journalEvent.Event);
        }
    }

    private bool ProcessLoadGameEvent(JournalEvent journalEvent)
    {
        try
        {
            var shipType = ExtractStringProperty(journalEvent, "Ship");
            var shipName = ExtractStringProperty(journalEvent, "ShipName");
            var shipIdent = ExtractStringProperty(journalEvent, "ShipIdent");
            var shipId = journalEvent.ShipID ?? ExtractLongProperty(journalEvent, "ShipID");
            var hullValue = ExtractLongProperty(journalEvent, "HullValue");
            var modulesValue = ExtractLongProperty(journalEvent, "ModulesValue");

            if (!string.IsNullOrEmpty(shipType))
            {
                var newShip = new CurrentShip
                {
                    ShipType = shipType,
                    ShipName = shipName ?? shipIdent ?? shipType,
                    ShipIdent = shipIdent,
                    ShipID = shipId,
                    HullValue = hullValue,
                    ModulesValue = modulesValue,
                    LastUpdated = journalEvent.Timestamp
                };

                return UpdateCurrentShip(newShip);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing LoadGame event for ship tracking");
        }
        
        return false;
    }

    private bool ProcessShipyardSwapEvent(JournalEvent journalEvent)
    {
        try
        {
            var shipType = ExtractStringProperty(journalEvent, "ShipType");
            var shipName = ExtractStringProperty(journalEvent, "ShipName");
            var shipId = ExtractLongProperty(journalEvent, "ShipID");

            if (!string.IsNullOrEmpty(shipType))
            {
                var newShip = new CurrentShip
                {
                    ShipType = shipType,
                    ShipName = shipName ?? shipType,
                    ShipID = shipId,
                    LastUpdated = journalEvent.Timestamp
                };

                _logger.LogDebug("Processing ShipyardSwap: {ShipType} (ID: {ShipID})", shipType, shipId);
                return UpdateCurrentShip(newShip);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing ShipyardSwap event for ship tracking");
        }
        
        return false;
    }

    private bool ProcessLoadoutEvent(JournalEvent journalEvent)
    {
        try
        {
            var shipType = ExtractStringProperty(journalEvent, "Ship");
            var shipName = ExtractStringProperty(journalEvent, "ShipName");
            var shipIdent = ExtractStringProperty(journalEvent, "ShipIdent");
            var shipId = journalEvent.ShipID ?? ExtractLongProperty(journalEvent, "ShipID");
            var hullValue = ExtractLongProperty(journalEvent, "HullValue");
            var modulesValue = ExtractLongProperty(journalEvent, "ModulesValue");
            var hullHealth = ExtractDoubleProperty(journalEvent, "HullHealth");

            if (!string.IsNullOrEmpty(shipType))
            {
                var newShip = new CurrentShip
                {
                    ShipType = shipType,
                    ShipName = shipName ?? shipIdent ?? shipType,
                    ShipIdent = shipIdent,
                    ShipID = shipId,
                    HullValue = hullValue,
                    ModulesValue = modulesValue,
                    HullHealth = hullHealth,
                    LastUpdated = journalEvent.Timestamp
                };

                _logger.LogDebug("Processing Loadout: {ShipType} '{ShipName}' (ID: {ShipID})", shipType, shipName, shipId);
                return UpdateCurrentShip(newShip);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing Loadout event for ship tracking");
        }
        
        return false;
    }

    private void ProcessStoredShipsEvent(JournalEvent journalEvent)
    {
        try
        {
            // StoredShips event contains current ship info in the ShipsHere or ShipsRemote arrays
            // This is mainly for informational purposes - we'll log it but not necessarily change current ship
            _logger.LogDebug("Received StoredShips event - ship inventory updated");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing StoredShips event");
        }
    }

    private bool UpdateCurrentShipFromEvent(JournalEvent journalEvent)
    {
        try
        {
            // Only update if we have more complete information or no current ship
            if (_currentShip == null || 
                (!string.IsNullOrEmpty(journalEvent.Ship) && journalEvent.Ship != _currentShip.ShipType) ||
                (journalEvent.ShipID.HasValue && journalEvent.ShipID != _currentShip.ShipID))
            {
                var shipType = journalEvent.Ship ?? _currentShip?.ShipType ?? "Unknown";
                var shipId = journalEvent.ShipID ?? _currentShip?.ShipID;

                var newShip = new CurrentShip
                {
                    ShipType = shipType,
                    ShipName = _currentShip?.ShipName ?? shipType,
                    ShipIdent = _currentShip?.ShipIdent,
                    ShipID = shipId,
                    HullValue = _currentShip?.HullValue,
                    ModulesValue = _currentShip?.ModulesValue,
                    HullHealth = journalEvent.Health ?? _currentShip?.HullHealth,
                    LastUpdated = journalEvent.Timestamp
                };

                return UpdateCurrentShip(newShip);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error updating current ship from event");
        }
        
        return false;
    }

    private bool UpdateCurrentShip(CurrentShip newShip)
    {
        if (_currentShip == null || 
            !_currentShip.IsSameShip(newShip) ||
            newShip.LastUpdated > _currentShip.LastUpdated)
        {
            _currentShip = newShip;
            return true;
        }
        return false;
    }

    // Helper methods to safely extract properties from AdditionalData
    private string? ExtractStringProperty(JournalEvent journalEvent, string propertyName)
    {
        if (journalEvent.AdditionalData?.TryGetValue(propertyName, out var value) == true)
        {
            return value?.ToString();
        }
        return null;
    }

    private long? ExtractLongProperty(JournalEvent journalEvent, string propertyName)
    {
        if (journalEvent.AdditionalData?.TryGetValue(propertyName, out var value) == true)
        {
            if (value is JsonElement element && element.ValueKind == JsonValueKind.Number)
            {
                return element.GetInt64();
            }
            if (long.TryParse(value?.ToString(), out var longValue))
            {
                return longValue;
            }
        }
        return null;
    }

    private double? ExtractDoubleProperty(JournalEvent journalEvent, string propertyName)
    {
        if (journalEvent.AdditionalData?.TryGetValue(propertyName, out var value) == true)
        {
            if (value is JsonElement element && element.ValueKind == JsonValueKind.Number)
            {
                return element.GetDouble();
            }
            if (double.TryParse(value?.ToString(), out var doubleValue))
            {
                return doubleValue;
            }
        }
        return null;
    }

    public void Reset()
    {
        _currentShip = null;
        _logger.LogInformation("Ship tracking reset");
    }
}

public class CurrentShip
{
    public string ShipType { get; set; } = string.Empty;
    public string ShipName { get; set; } = string.Empty;
    public string? ShipIdent { get; set; }
    public long? ShipID { get; set; }
    public long? HullValue { get; set; }
    public long? ModulesValue { get; set; }
    public double? HullHealth { get; set; }
    public DateTime LastUpdated { get; set; }

    public bool IsSameShip(CurrentShip other)
    {
        // Compare by ShipID if both have it, otherwise compare by ShipType and ShipName
        if (ShipID.HasValue && other.ShipID.HasValue)
        {
            return ShipID == other.ShipID;
        }
        
        return ShipType.Equals(other.ShipType, StringComparison.OrdinalIgnoreCase) &&
               ShipName.Equals(other.ShipName, StringComparison.OrdinalIgnoreCase);
    }

    public string GetDisplayName()
    {
        if (!string.IsNullOrEmpty(ShipName) && !ShipName.Equals(ShipType, StringComparison.OrdinalIgnoreCase))
        {
            return $"{ShipName} ({ShipType})";
        }
        return ShipType;
    }

    public string GetShipKey()
    {
        // Create a consistent key for pattern storage
        if (ShipID.HasValue)
        {
            return $"{ShipType}_{ShipID}";
        }
        return $"{ShipType}_{ShipName}".Replace(" ", "_");
    }
}