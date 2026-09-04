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
    private readonly SettingsPersistenceService _settingsPersistence;

    public UserSettingsController(
        ILogger<UserSettingsController> logger,
        UserSettingsService userSettingsService,
        AppSettings appSettings,
        SettingsPersistenceService settingsPersistence)
    {
        _logger = logger;
        _userSettingsService = userSettingsService;
        _appSettings = appSettings;
        _settingsPersistence = settingsPersistence;
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

    /// <summary>
    /// The settings the UI saves. This route does not touch the running configuration or the
    /// settings file itself: the request becomes an update handed to the one service that validates
    /// it, applies what can be applied now, and writes it atomically - so a rejected value leaves
    /// both the running configuration and the saved one exactly as they were, and a 200 says plainly
    /// which parts are live already and which need a restart.
    /// </summary>
    [HttpPost("save")]
    public async Task<ActionResult> SaveUserSettings([FromBody] SaveUserSettingsRequest request)
    {
        try
        {
            _logger.LogInformation("Saving user settings");

            var result = await _settingsPersistence.ApplyAsync(new SettingsUpdate
            {
                AudioDeviceId = request.AudioDeviceId,
                AudioDeviceEndpointId = request.AudioDeviceEndpointId,
                AudioDeviceName = request.AudioDeviceName,
                MaxIntensity = request.MaxIntensity,
                DefaultFrequency = request.DefaultFrequency,

                JournalPath = request.JournalPath,
                MonitorLatestOnly = request.MonitorLatestOnly,

                ContextualIntelligenceEnabled = request.ContextualIntelligenceEnabled,
                EnableAdaptiveIntensity = request.EnableAdaptiveIntensity,
                EnablePredictivePatterns = request.EnablePredictivePatterns,
                EnableContextualVoice = request.EnableContextualVoice
            });

            if (!result.Valid)
            {
                return BadRequest(new
                {
                    error = "Failed to save user settings",
                    message = result.Message,
                    validation_errors = result.ValidationErrors,
                    settings = result.ToPayload()
                });
            }

            _logger.LogInformation("User settings save handled: {Message}", result.Message);

            // A change that only reached memory is not a saved setting: the caller has to hear that.
            return StatusCode(result.Saved ? 200 : 500, new
            {
                message = result.Message,
                timestamp = DateTime.UtcNow,
                settingsPath = result.SettingsPath,
                settings = result.ToPayload()
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
            _appSettings.Audio.AudioDeviceEndpointId = string.Empty;
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
                    DeviceEndpointId = _appSettings.Audio.AudioDeviceEndpointId,
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

    /// <summary>Endpoint id of the chosen output; empty means the system default.</summary>
    public string? AudioDeviceEndpointId { get; set; }
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
    public string DeviceEndpointId { get; set; } = string.Empty;
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