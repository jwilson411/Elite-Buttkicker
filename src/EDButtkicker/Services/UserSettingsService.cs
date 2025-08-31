using System.Text.Json;
using Microsoft.Extensions.Logging;
using EDButtkicker.Configuration;

namespace EDButtkicker.Services;

public class UserSettingsService
{
    private readonly ILogger<UserSettingsService> _logger;
    private readonly string _userSettingsPath;
    private readonly JsonSerializerOptions _jsonOptions;

    public UserSettingsService(ILogger<UserSettingsService> logger)
    {
        _logger = logger;
        
        // Create user-specific settings directory
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var settingsDir = Path.Combine(appDataPath, "EDButtkicker");
        Directory.CreateDirectory(settingsDir);
        
        _userSettingsPath = Path.Combine(settingsDir, "user-settings.json");
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            AllowTrailingCommas = true
        };
        
        _logger.LogDebug("UserSettingsService initialized with path: {SettingsPath}", _userSettingsPath);
    }

    public async Task<UserPreferences> LoadUserPreferencesAsync()
    {
        try
        {
            if (!File.Exists(_userSettingsPath))
            {
                _logger.LogInformation("No user settings file found, using defaults");
                return new UserPreferences();
            }

            var json = await File.ReadAllTextAsync(_userSettingsPath);
            var preferences = JsonSerializer.Deserialize<UserPreferences>(json, _jsonOptions);
            
            if (preferences == null)
            {
                _logger.LogWarning("Failed to deserialize user preferences, using defaults");
                return new UserPreferences();
            }

            _logger.LogInformation("Loaded user preferences from {SettingsPath}", _userSettingsPath);
            _logger.LogDebug("Audio Device: {DeviceName} (ID: {DeviceId})", 
                preferences.AudioDeviceName, preferences.AudioDeviceId);
            
            return preferences;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading user preferences, using defaults");
            return new UserPreferences();
        }
    }

    public async Task SaveUserPreferencesAsync(UserPreferences preferences)
    {
        try
        {
            var json = JsonSerializer.Serialize(preferences, _jsonOptions);
            await File.WriteAllTextAsync(_userSettingsPath, json);
            
            _logger.LogInformation("Saved user preferences to {SettingsPath}", _userSettingsPath);
            _logger.LogDebug("Saved Audio Device: {DeviceName} (ID: {DeviceId})", 
                preferences.AudioDeviceName, preferences.AudioDeviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving user preferences");
            throw;
        }
    }

    public void ApplyUserPreferencesToAppSettings(UserPreferences preferences, AppSettings appSettings)
    {
        try
        {
            // Apply audio preferences
            if (preferences.AudioDeviceId.HasValue)
            {
                appSettings.Audio.AudioDeviceId = preferences.AudioDeviceId.Value;
                _logger.LogDebug("Applied audio device ID: {DeviceId}", preferences.AudioDeviceId.Value);
            }

            if (!string.IsNullOrEmpty(preferences.AudioDeviceName))
            {
                appSettings.Audio.AudioDeviceName = preferences.AudioDeviceName;
                _logger.LogDebug("Applied audio device name: {DeviceName}", preferences.AudioDeviceName);
            }

            if (preferences.MaxIntensity.HasValue)
            {
                appSettings.Audio.MaxIntensity = preferences.MaxIntensity.Value;
                _logger.LogDebug("Applied max intensity: {MaxIntensity}", preferences.MaxIntensity.Value);
            }

            if (preferences.DefaultFrequency.HasValue)
            {
                appSettings.Audio.DefaultFrequency = preferences.DefaultFrequency.Value;
                _logger.LogDebug("Applied default frequency: {DefaultFrequency}", preferences.DefaultFrequency.Value);
            }

            // Apply journal preferences
            if (!string.IsNullOrEmpty(preferences.JournalPath))
            {
                appSettings.EliteDangerous.JournalPath = preferences.JournalPath;
                _logger.LogDebug("Applied journal path: {JournalPath}", preferences.JournalPath);
            }

            if (preferences.MonitorLatestOnly.HasValue)
            {
                appSettings.EliteDangerous.MonitorLatestOnly = preferences.MonitorLatestOnly.Value;
                _logger.LogDebug("Applied monitor latest only: {MonitorLatestOnly}", preferences.MonitorLatestOnly.Value);
            }

            // Apply contextual intelligence preferences
            if (appSettings.ContextualIntelligence != null && preferences.ContextualIntelligence != null)
            {
                if (preferences.ContextualIntelligence.Enabled.HasValue)
                {
                    appSettings.ContextualIntelligence.Enabled = preferences.ContextualIntelligence.Enabled.Value;
                }
                
                if (preferences.ContextualIntelligence.EnableAdaptiveIntensity.HasValue)
                {
                    appSettings.ContextualIntelligence.EnableAdaptiveIntensity = preferences.ContextualIntelligence.EnableAdaptiveIntensity.Value;
                }
                
                if (preferences.ContextualIntelligence.EnablePredictivePatterns.HasValue)
                {
                    appSettings.ContextualIntelligence.EnablePredictivePatterns = preferences.ContextualIntelligence.EnablePredictivePatterns.Value;
                }
                
                if (preferences.ContextualIntelligence.EnableContextualVoice.HasValue)
                {
                    appSettings.ContextualIntelligence.EnableContextualVoice = preferences.ContextualIntelligence.EnableContextualVoice.Value;
                }
            }

            _logger.LogInformation("Applied user preferences to app settings");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying user preferences to app settings");
        }
    }

    public UserPreferences CreatePreferencesFromAppSettings(AppSettings appSettings)
    {
        try
        {
            var preferences = new UserPreferences
            {
                AudioDeviceId = appSettings.Audio.AudioDeviceId,
                AudioDeviceName = appSettings.Audio.AudioDeviceName,
                MaxIntensity = appSettings.Audio.MaxIntensity,
                DefaultFrequency = appSettings.Audio.DefaultFrequency,
                JournalPath = appSettings.EliteDangerous.JournalPath,
                MonitorLatestOnly = appSettings.EliteDangerous.MonitorLatestOnly,
                ContextualIntelligence = appSettings.ContextualIntelligence != null ? new UserContextualIntelligencePreferences
                {
                    Enabled = appSettings.ContextualIntelligence.Enabled,
                    EnableAdaptiveIntensity = appSettings.ContextualIntelligence.EnableAdaptiveIntensity,
                    EnablePredictivePatterns = appSettings.ContextualIntelligence.EnablePredictivePatterns,
                    EnableContextualVoice = appSettings.ContextualIntelligence.EnableContextualVoice
                } : null,
                LastSaved = DateTime.UtcNow
            };

            _logger.LogDebug("Created preferences from app settings");
            return preferences;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating preferences from app settings");
            return new UserPreferences();
        }
    }

    public string GetUserSettingsPath() => _userSettingsPath;

    public bool UserSettingsExist() => File.Exists(_userSettingsPath);
}

public class UserPreferences
{
    // Audio preferences
    public int? AudioDeviceId { get; set; }
    public string? AudioDeviceName { get; set; }
    public int? MaxIntensity { get; set; }
    public int? DefaultFrequency { get; set; }

    // Elite Dangerous preferences
    public string? JournalPath { get; set; }
    public bool? MonitorLatestOnly { get; set; }

    // Contextual Intelligence preferences
    public UserContextualIntelligencePreferences? ContextualIntelligence { get; set; }

    // Metadata
    public DateTime? LastSaved { get; set; }
    public string? Version { get; set; } = "1.0.0";
}

public class UserContextualIntelligencePreferences
{
    public bool? Enabled { get; set; }
    public bool? EnableAdaptiveIntensity { get; set; }
    public bool? EnablePredictivePatterns { get; set; }
    public bool? EnableContextualVoice { get; set; }
}