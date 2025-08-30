using Microsoft.AspNetCore.Http;
using System.Text.Json;
using EDButtkicker.Configuration;
using Microsoft.Extensions.Logging;

namespace EDButtkicker.Controllers;

public class ConfigurationApiController
{
    private readonly ILogger<ConfigurationApiController> _logger;
    private readonly AppSettings _settings;

    public ConfigurationApiController(ILogger<ConfigurationApiController> logger, AppSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public async Task GetConfiguration(HttpContext context)
    {
        try
        {
            var config = new
            {
                EliteDangerous = _settings.EliteDangerous,
                Audio = _settings.Audio,
                Version = "1.0.0",
                Features = new
                {
                    AdvancedPatterns = true,
                    VoiceIntegration = true,
                    MultiLayerSupport = true,
                    IntensityCurves = true,
                    PatternChaining = true,
                    ConditionalLogic = true
                }
            };

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(config, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting configuration");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    public async Task UpdateConfiguration(HttpContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var json = await reader.ReadToEndAsync();
            
            if (string.IsNullOrEmpty(json))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Request body is empty" }));
                return;
            }

            var configUpdate = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (configUpdate == null)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Invalid JSON format" }));
                return;
            }

            // Update individual settings
            if (configUpdate.ContainsKey("audio"))
            {
                var audioJson = configUpdate["audio"].ToString();
                if (!string.IsNullOrEmpty(audioJson))
                {
                    var audioSettings = JsonSerializer.Deserialize<Dictionary<string, object>>(audioJson);
                    if (audioSettings != null)
                    {
                        if (audioSettings.ContainsKey("AudioDeviceId") && int.TryParse(audioSettings["AudioDeviceId"].ToString(), out int deviceId))
                            _settings.Audio.AudioDeviceId = deviceId;
                        if (audioSettings.ContainsKey("AudioDeviceName"))
                            _settings.Audio.AudioDeviceName = audioSettings["AudioDeviceName"].ToString() ?? _settings.Audio.AudioDeviceName;
                        if (audioSettings.ContainsKey("MaxIntensity") && int.TryParse(audioSettings["MaxIntensity"].ToString(), out int maxIntensity))
                            _settings.Audio.MaxIntensity = maxIntensity;
                        if (audioSettings.ContainsKey("DefaultFrequency") && int.TryParse(audioSettings["DefaultFrequency"].ToString(), out int defaultFreq))
                            _settings.Audio.DefaultFrequency = defaultFreq;
                    }
                }
            }

            if (configUpdate.ContainsKey("eliteDangerous"))
            {
                var edJson = configUpdate["eliteDangerous"].ToString();
                if (!string.IsNullOrEmpty(edJson))
                {
                    var edSettings = JsonSerializer.Deserialize<Dictionary<string, object>>(edJson);
                    if (edSettings != null)
                    {
                        if (edSettings.ContainsKey("JournalPath"))
                            _settings.EliteDangerous.JournalPath = edSettings["JournalPath"].ToString() ?? _settings.EliteDangerous.JournalPath;
                        if (edSettings.ContainsKey("MonitorLatestOnly") && bool.TryParse(edSettings["MonitorLatestOnly"].ToString(), out bool monitorLatest))
                            _settings.EliteDangerous.MonitorLatestOnly = monitorLatest;
                    }
                }
            }

            _logger.LogInformation("Configuration updated via web interface");

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = true, message = "Configuration updated successfully" }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating configuration");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    public async Task ExportConfiguration(HttpContext context)
    {
        try
        {
            var exportData = new
            {
                timestamp = DateTime.UtcNow,
                version = "1.0.0",
                configuration = new
                {
                    EliteDangerous = _settings.EliteDangerous,
                    Audio = _settings.Audio
                },
                metadata = new
                {
                    exported_by = "Elite Dangerous Buttkicker Extension",
                    export_type = "full_configuration"
                }
            };

            var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions { WriteIndented = true });
            
            context.Response.ContentType = "application/json";
            context.Response.Headers.Add("Content-Disposition", $"attachment; filename=\"ed-buttkicker-config-{DateTime.Now:yyyyMMdd-HHmmss}.json\"");
            
            await context.Response.WriteAsync(json);
            
            _logger.LogInformation("Configuration exported via web interface");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting configuration");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    public async Task ImportConfiguration(HttpContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var json = await reader.ReadToEndAsync();
            
            if (string.IsNullOrEmpty(json))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "No configuration data provided" }));
                return;
            }

            var importData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (importData?.ContainsKey("configuration") != true)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Invalid configuration format" }));
                return;
            }

            var configJson = importData["configuration"].ToString();
            if (string.IsNullOrEmpty(configJson))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Configuration data is empty" }));
                return;
            }

            var configData = JsonSerializer.Deserialize<Dictionary<string, object>>(configJson);
            if (configData == null)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Failed to parse configuration data" }));
                return;
            }

            // Import audio settings
            if (configData.ContainsKey("Audio"))
            {
                var audioJson = configData["Audio"].ToString();
                if (!string.IsNullOrEmpty(audioJson))
                {
                    var audioSettings = JsonSerializer.Deserialize<Dictionary<string, object>>(audioJson);
                    if (audioSettings != null)
                    {
                        if (audioSettings.ContainsKey("MaxIntensity") && int.TryParse(audioSettings["MaxIntensity"].ToString(), out int maxIntensity))
                            _settings.Audio.MaxIntensity = maxIntensity;
                        if (audioSettings.ContainsKey("DefaultFrequency") && int.TryParse(audioSettings["DefaultFrequency"].ToString(), out int defaultFreq))
                            _settings.Audio.DefaultFrequency = defaultFreq;
                        if (audioSettings.ContainsKey("SampleRate") && int.TryParse(audioSettings["SampleRate"].ToString(), out int sampleRate))
                            _settings.Audio.SampleRate = sampleRate;
                        if (audioSettings.ContainsKey("BufferSize") && int.TryParse(audioSettings["BufferSize"].ToString(), out int bufferSize))
                            _settings.Audio.BufferSize = bufferSize;
                    }
                }
            }

            // Import Elite Dangerous settings
            if (configData.ContainsKey("EliteDangerous"))
            {
                var edJson = configData["EliteDangerous"].ToString();
                if (!string.IsNullOrEmpty(edJson))
                {
                    var edSettings = JsonSerializer.Deserialize<Dictionary<string, object>>(edJson);
                    if (edSettings != null)
                    {
                        if (edSettings.ContainsKey("MonitorLatestOnly") && bool.TryParse(edSettings["MonitorLatestOnly"].ToString(), out bool monitorLatest))
                            _settings.EliteDangerous.MonitorLatestOnly = monitorLatest;
                        // Don't auto-import journal path for security
                    }
                }
            }

            _logger.LogInformation("Configuration imported via web interface");

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new 
            { 
                success = true, 
                message = "Configuration imported successfully",
                imported_at = DateTime.UtcNow
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing configuration");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }
}