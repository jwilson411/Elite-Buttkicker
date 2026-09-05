using Microsoft.AspNetCore.Http;
using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Hosting;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging;

namespace EDButtkicker.Controllers;

public class ConfigurationApiController
{
    private readonly ILogger<ConfigurationApiController> _logger;
    private readonly AppSettings _settings;
    private readonly SettingsPersistenceService _settingsPersistence;

    public ConfigurationApiController(
        ILogger<ConfigurationApiController> logger,
        AppSettings settings,
        SettingsPersistenceService settingsPersistence)
    {
        _logger = logger;
        _settings = settings;
        _settingsPersistence = settingsPersistence;
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

    /// <summary>
    /// The settings the web UI edits. Nothing is written to the running configuration here: the
    /// request is turned into an update and handed to the one service that validates it, applies
    /// what can be applied now, and persists the result - so a 200 here is a change that survives a
    /// restart, and the response says which parts are live already.
    /// </summary>
    public async Task UpdateConfiguration(HttpContext context)
    {
        try
        {
            var (handled, root) = await ReadJsonAsync(context);
            if (handled)
            {
                return;
            }

            if (root == null)
            {
                await WriteBadRequestAsync(context, "Request body is empty");
                return;
            }

            if (root.Value.ValueKind != JsonValueKind.Object)
            {
                await WriteBadRequestAsync(context, "Invalid JSON format");
                return;
            }

            var update = new SettingsUpdate();

            if (TryGetSection(root.Value, "audio", out var audio))
            {
                update.AudioDeviceId = ReadInt(audio, "AudioDeviceId");
                update.AudioDeviceEndpointId = ReadString(audio, "AudioDeviceEndpointId");
                update.AudioDeviceName = ReadString(audio, "AudioDeviceName");
                update.MaxIntensity = ReadInt(audio, "MaxIntensity");
                update.DefaultFrequency = ReadInt(audio, "DefaultFrequency");
                update.SampleRate = ReadInt(audio, "SampleRate");
                update.BufferSize = ReadInt(audio, "BufferSize");
            }

            if (TryGetSection(root.Value, "eliteDangerous", out var eliteDangerous))
            {
                update.JournalPath = ReadString(eliteDangerous, "JournalPath");
                update.MonitorLatestOnly = ReadBool(eliteDangerous, "MonitorLatestOnly");
            }

            var result = await _settingsPersistence.ApplyAsync(update);

            if (!result.Valid)
            {
                await WriteBadRequestAsync(context, result.Message, result);
                return;
            }

            _logger.LogInformation("Configuration updated via web interface: {Message}", result.Message);

            // A change that only reached memory is not a successful configuration change: say so.
            context.Response.StatusCode = result.Saved ? 200 : 500;
            await WriteJsonAsync(context, new
            {
                success = result.Saved,
                message = result.Message,
                settings = result.ToPayload()
            });
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
            var (handled, root) = await ReadJsonAsync(context);
            if (handled)
            {
                return;
            }

            if (root == null)
            {
                await WriteBadRequestAsync(context, "No configuration data provided");
                return;
            }

            if (root.Value.ValueKind != JsonValueKind.Object ||
                !TryGetSection(root.Value, "configuration", out var configuration))
            {
                await WriteBadRequestAsync(context, "Invalid configuration format");
                return;
            }

            var update = new SettingsUpdate();

            if (TryGetSection(configuration, "Audio", out var audio))
            {
                update.MaxIntensity = ReadInt(audio, "MaxIntensity");
                update.DefaultFrequency = ReadInt(audio, "DefaultFrequency");
                update.SampleRate = ReadInt(audio, "SampleRate");
                update.BufferSize = ReadInt(audio, "BufferSize");
            }

            if (TryGetSection(configuration, "EliteDangerous", out var eliteDangerous))
            {
                update.MonitorLatestOnly = ReadBool(eliteDangerous, "MonitorLatestOnly");
                // The journal path is still deliberately not imported: an exported file names a
                // folder on the machine it came from.
            }

            var result = await _settingsPersistence.ApplyAsync(update);

            if (!result.Valid)
            {
                await WriteBadRequestAsync(context, result.Message, result);
                return;
            }

            _logger.LogInformation("Configuration imported via web interface: {Message}", result.Message);

            context.Response.StatusCode = result.Saved ? 200 : 500;
            await WriteJsonAsync(context, new
            {
                success = result.Saved,
                message = result.Message,
                imported_at = DateTime.UtcNow,
                settings = result.ToPayload()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing configuration");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    /// <summary>
    /// The request body as JSON, read under the shared byte cap. <c>Handled</c> means the response
    /// is already written - 413 for a body over the cap, 400 for JSON this API will not parse - and
    /// a null <c>Root</c> with <c>Handled</c> false means the caller sent no body at all.
    /// </summary>
    private static async Task<(bool Handled, JsonElement? Root)> ReadJsonAsync(HttpContext context)
    {
        var body = await BoundedRequestReader.ReadAsync(context);

        switch (body.Status)
        {
            case BoundedBodyStatus.TooLarge:
                await BoundedRequestReader.WriteTooLargeAsync(context);
                return (true, null);

            case BoundedBodyStatus.Empty:
                return (false, null);
        }

        if (!BoundedRequestReader.TryParseDocument(body.Text, out var root))
        {
            await WriteBadRequestAsync(context, "Request body is not valid JSON");
            return (true, null);
        }

        return (false, root);
    }

    /// <summary>
    /// A nested object by name, case-insensitively: the web UI sends camelCase and an exported
    /// configuration file carries the PascalCase property names of the settings classes.
    /// </summary>
    private static bool TryGetSection(JsonElement parent, string name, out JsonElement section)
    {
        if (parent.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in parent.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    section = property.Value;
                    return true;
                }
            }
        }

        section = default;
        return false;
    }

    private static bool TryGetValue(JsonElement section, string name, out JsonElement value)
    {
        if (section.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in section.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static int? ReadInt(JsonElement section, string name)
    {
        if (!TryGetValue(section, name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static bool? ReadBool(JsonElement section, string name)
    {
        if (!TryGetValue(section, name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ReadString(JsonElement section, string name) =>
        TryGetValue(section, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Task WriteJsonAsync(HttpContext context, object payload)
    {
        context.Response.ContentType = "application/json";

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static Task WriteBadRequestAsync(HttpContext context, string error, SettingsUpdateResult? result = null)
    {
        context.Response.StatusCode = 400;

        return WriteJsonAsync(context, new
        {
            error,
            // Named individually so the UI can show which value was refused, and why.
            validation_errors = result?.ValidationErrors ?? Array.Empty<string>(),
            settings = result?.ToPayload()
        });
    }
}