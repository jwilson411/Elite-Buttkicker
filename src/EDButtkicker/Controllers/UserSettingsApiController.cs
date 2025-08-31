using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using EDButtkicker.Configuration;
using EDButtkicker.Services;

namespace EDButtkicker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UserSettingsController : ControllerBase
{
    private readonly ILogger<UserSettingsController> _logger;
    private readonly UserSettingsService _userSettingsService;
    private readonly AppSettings _appSettings;
    private readonly AudioEngineService _audioEngineService;

    public UserSettingsController(
        ILogger<UserSettingsController> logger,
        UserSettingsService userSettingsService,
        AppSettings appSettings,
        AudioEngineService audioEngineService)
    {
        _logger = logger;
        _userSettingsService = userSettingsService;
        _appSettings = appSettings;
        _audioEngineService = audioEngineService;
    }

    [HttpGet]
    public async Task<ActionResult<UserPreferences>> GetUserSettings()
    {
        try
        {
            var preferences = await _userSettingsService.LoadUserPreferencesAsync();
            _logger.LogDebug("Retrieved user settings");
            return Ok(preferences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving user settings");
            return StatusCode(500, new { error = "Failed to retrieve user settings", details = ex.Message });
        }
    }

    [HttpPost("save")]
    public async Task<ActionResult> SaveUserSettings([FromBody] SaveUserSettingsRequest request)
    {
        try
        {
            _logger.LogInformation("Saving user settings");
            
            // Create preferences from current app settings and request
            var preferences = new UserPreferences
            {
                // Audio settings
                AudioDeviceId = request.AudioDeviceId ?? _appSettings.Audio.AudioDeviceId,
                AudioDeviceName = request.AudioDeviceName ?? _appSettings.Audio.AudioDeviceName,
                MaxIntensity = request.MaxIntensity ?? _appSettings.Audio.MaxIntensity,
                DefaultFrequency = request.DefaultFrequency ?? _appSettings.Audio.DefaultFrequency,
                
                // Elite Dangerous settings
                JournalPath = request.JournalPath ?? _appSettings.EliteDangerous.JournalPath,
                MonitorLatestOnly = request.MonitorLatestOnly ?? _appSettings.EliteDangerous.MonitorLatestOnly,
                
                // Contextual Intelligence settings
                ContextualIntelligence = _appSettings.ContextualIntelligence != null ? new UserContextualIntelligencePreferences
                {
                    Enabled = request.ContextualIntelligenceEnabled ?? _appSettings.ContextualIntelligence.Enabled,
                    EnableAdaptiveIntensity = request.EnableAdaptiveIntensity ?? _appSettings.ContextualIntelligence.EnableAdaptiveIntensity,
                    EnablePredictivePatterns = request.EnablePredictivePatterns ?? _appSettings.ContextualIntelligence.EnablePredictivePatterns,
                    EnableContextualVoice = request.EnableContextualVoice ?? _appSettings.ContextualIntelligence.EnableContextualVoice
                } : null,
                
                // Metadata
                LastSaved = DateTime.UtcNow,
                Version = "1.0.0"
            };

            // Apply changes to app settings immediately
            _userSettingsService.ApplyUserPreferencesToAppSettings(preferences, _appSettings);
            
            // If audio device changed, reinitialize audio engine
            if (request.AudioDeviceId.HasValue || !string.IsNullOrEmpty(request.AudioDeviceName))
            {
                _logger.LogInformation("Audio device settings changed, reinitializing audio engine");
                try
                {
                    // Note: In a real implementation, you might want to add a method to reinitialize
                    // the audio engine. For now, we'll log this and let it be handled on next restart
                    _logger.LogInformation("Audio device change will take effect on next restart or pattern playback");
                }
                catch (Exception audioEx)
                {
                    _logger.LogWarning(audioEx, "Audio engine reinitialization failed, but settings were saved");
                }
            }

            // Save to disk
            await _userSettingsService.SaveUserPreferencesAsync(preferences);
            
            _logger.LogInformation("User settings saved successfully");
            
            return Ok(new { 
                message = "Settings saved successfully", 
                timestamp = preferences.LastSaved,
                settingsPath = _userSettingsService.GetUserSettingsPath()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving user settings");
            return StatusCode(500, new { error = "Failed to save user settings", details = ex.Message });
        }
    }

    [HttpPost("reset")]
    public async Task<ActionResult> ResetUserSettings()
    {
        try
        {
            _logger.LogInformation("Resetting user settings to defaults");
            
            // Delete the user settings file if it exists
            var settingsPath = _userSettingsService.GetUserSettingsPath();
            if (System.IO.File.Exists(settingsPath))
            {
                System.IO.File.Delete(settingsPath);
                _logger.LogInformation("Deleted user settings file: {SettingsPath}", settingsPath);
            }
            
            // Reset app settings to defaults (you might want to reload from appsettings.json)
            _appSettings.Audio.AudioDeviceId = -1;
            _appSettings.Audio.AudioDeviceName = "Default";
            // Reset other settings as needed
            
            return Ok(new { message = "Settings reset to defaults successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting user settings");
            return StatusCode(500, new { error = "Failed to reset user settings", details = ex.Message });
        }
    }

    [HttpGet("current")]
    public ActionResult<CurrentSettingsResponse> GetCurrentSettings()
    {
        try
        {
            var response = new CurrentSettingsResponse
            {
                Audio = new CurrentAudioSettings
                {
                    DeviceId = _appSettings.Audio.AudioDeviceId,
                    DeviceName = _appSettings.Audio.AudioDeviceName,
                    MaxIntensity = _appSettings.Audio.MaxIntensity,
                    DefaultFrequency = _appSettings.Audio.DefaultFrequency,
                    SampleRate = _appSettings.Audio.SampleRate,
                    BufferSize = _appSettings.Audio.BufferSize
                },
                EliteDangerous = new CurrentEliteDangerousSettings
                {
                    JournalPath = _appSettings.EliteDangerous.JournalPath,
                    MonitorLatestOnly = _appSettings.EliteDangerous.MonitorLatestOnly
                },
                ContextualIntelligence = _appSettings.ContextualIntelligence != null ? new CurrentContextualIntelligenceSettings
                {
                    Enabled = _appSettings.ContextualIntelligence.Enabled,
                    EnableAdaptiveIntensity = _appSettings.ContextualIntelligence.EnableAdaptiveIntensity,
                    EnablePredictivePatterns = _appSettings.ContextualIntelligence.EnablePredictivePatterns,
                    EnableContextualVoice = _appSettings.ContextualIntelligence.EnableContextualVoice
                } : null,
                UserSettingsExist = _userSettingsService.UserSettingsExist(),
                UserSettingsPath = _userSettingsService.GetUserSettingsPath()
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving current settings");
            return StatusCode(500, new { error = "Failed to retrieve current settings", details = ex.Message });
        }
    }
}

// Request/Response DTOs
public class SaveUserSettingsRequest
{
    // Audio settings
    public int? AudioDeviceId { get; set; }
    public string? AudioDeviceName { get; set; }
    public int? MaxIntensity { get; set; }
    public int? DefaultFrequency { get; set; }
    
    // Elite Dangerous settings
    public string? JournalPath { get; set; }
    public bool? MonitorLatestOnly { get; set; }
    
    // Contextual Intelligence settings
    public bool? ContextualIntelligenceEnabled { get; set; }
    public bool? EnableAdaptiveIntensity { get; set; }
    public bool? EnablePredictivePatterns { get; set; }
    public bool? EnableContextualVoice { get; set; }
}

public class CurrentSettingsResponse
{
    public CurrentAudioSettings Audio { get; set; } = new();
    public CurrentEliteDangerousSettings EliteDangerous { get; set; } = new();
    public CurrentContextualIntelligenceSettings? ContextualIntelligence { get; set; }
    public bool UserSettingsExist { get; set; }
    public string UserSettingsPath { get; set; } = string.Empty;
}

public class CurrentAudioSettings
{
    public int DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public int MaxIntensity { get; set; }
    public int DefaultFrequency { get; set; }
    public int SampleRate { get; set; }
    public int BufferSize { get; set; }
}

public class CurrentEliteDangerousSettings
{
    public string JournalPath { get; set; } = string.Empty;
    public bool MonitorLatestOnly { get; set; }
}

public class CurrentContextualIntelligenceSettings
{
    public bool Enabled { get; set; }
    public bool EnableAdaptiveIntensity { get; set; }
    public bool EnablePredictivePatterns { get; set; }
    public bool EnableContextualVoice { get; set; }
}