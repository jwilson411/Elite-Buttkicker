using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization;
using EDButtkicker.Hosting;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging;

namespace EDButtkicker.Controllers;

/// <summary>
/// The built-in event patterns the UI lists, edits and plays. Routes live here as attributes,
/// so the routing table and this file cannot disagree about what /api/patterns serves.
/// </summary>
[ApiController]
[Route("api/patterns")]
public class PatternApiController : ControllerBase
{
    /// <summary>
    /// How a pattern arrives from the page and leaves again: camelCase names read case
    /// insensitively, enums by name, and the shared request depth cap.
    /// </summary>
    private static readonly JsonSerializerOptions PatternJson = new()
    {
        MaxDepth = RequestLimits.MaxJsonDepth,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: true) }
    };

    private readonly ILogger<PatternApiController> _logger;
    private readonly EventMappingService _eventMapping;
    private readonly AudioEngineService _audioEngine;
    private readonly PatternSequencer _patternSequencer;

    public PatternApiController(
        ILogger<PatternApiController> logger, 
        EventMappingService eventMapping,
        AudioEngineService audioEngine,
        PatternSequencer patternSequencer)
    {
        _logger = logger;
        _eventMapping = eventMapping;
        _audioEngine = audioEngine;
        _patternSequencer = patternSequencer;
    }

    [HttpGet]
    public async Task GetPatterns()
    {
        var context = HttpContext;

        try
        {
            // The live mappings, not the built-in catalogue: a pattern created, updated or deleted
            // through this controller has to be what the page lists afterwards.
            var patterns = _eventMapping.GetEventMappings()
                .ToDictionary(entry => entry.Key, entry => DescribeMapping(entry.Value));

            var response = new
            {
                patterns,
                metadata = new
                {
                    total_patterns = patterns.Count,
                    supported_events = patterns.Keys.ToArray(),
                    advanced_features = new
                    {
                        multi_layer_support = true,
                        intensity_curves = new[] { "Linear", "Exponential", "Logarithmic", "Sine", "Bounce", "Custom" },
                        waveform_types = new[] { "Sine", "Square", "Triangle", "Sawtooth", "Noise" },
                        pattern_types = new[] { "SharpPulse", "BuildupRumble", "SustainedRumble", "Oscillating", "Impact", "Fade", "MultiLayer", "Sequence" }
                    }
                }
            };

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(response, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting patterns");
            await ApiError.WriteAsync(context, 500, "Failed to get patterns");
        }
    }

    /// <summary>
    /// Maps a pattern to an event that has none, and writes the mappings out. An event that is
    /// already mapped answers 409: PUT is the path for an existing one, so a create never silently
    /// replaces a pattern the caller did not know was there.
    /// </summary>
    [HttpPost]
    public async Task CreatePattern()
    {
        var context = HttpContext;

        try
        {
            var json = await BoundedRequestReader.ReadOrRespondAsync(context, "Request body is empty");
            if (json == null)
            {
                return;
            }

            if (!BoundedRequestReader.TryParseDocument(json, out var body) ||
                body.ValueKind != JsonValueKind.Object)
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest, new { error = "Invalid JSON format" });
                return;
            }

            if (!body.TryGetProperty("eventType", out var eventTypeElement) ||
                !body.TryGetProperty("pattern", out var patternElement))
            {
                await WriteJsonAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    new { error = "Missing required fields: eventType, pattern" });
                return;
            }

            var eventType = eventTypeElement.ValueKind == JsonValueKind.String
                ? eventTypeElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(eventType))
            {
                await WriteJsonAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    new { error = "EventType and pattern cannot be empty" });
                return;
            }

            if (eventType.Length > RequestLimits.MaxStringLength)
            {
                await WriteJsonAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    new { error = $"EventType must not exceed {RequestLimits.MaxStringLength} characters" });
                return;
            }

            if (!TryReadPattern(patternElement, out var pattern, out var patternErrors))
            {
                await WriteJsonAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    new { error = "Invalid pattern definition", errors = patternErrors });
                return;
            }

            var change = _eventMapping.AddEventMapping(new EventMapping
            {
                EventType = eventType,
                Pattern = pattern!,
                Enabled = ReadEnabled(body) ?? true
            });

            if (!change.IsApplied)
            {
                await WriteChangeFailureAsync(context, change);
                return;
            }

            // Read back what was stored instead of echoing the request: the response describes the
            // mapping the service actually kept.
            var stored = _eventMapping.GetEventMapping(eventType);
            if (stored == null)
            {
                _logger.LogError("Pattern for {EventType} was created but could not be read back", eventType);
                await ApiError.WriteAsync(context, 500, "The pattern was not stored");
                return;
            }

            _logger.LogInformation("Created the pattern mapping for event: {EventType}", eventType);

            await WriteJsonAsync(context, StatusCodes.Status201Created, new
            {
                success = true,
                message = $"Pattern for {eventType} created successfully",
                eventType,
                mapping = DescribeMapping(stored)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating pattern");
            await ApiError.WriteAsync(context, 500, "Failed to create the pattern");
        }
    }

    /// <summary>
    /// Replaces the pattern mapped to an existing event, and writes the mappings out. An event with
    /// no mapping answers 404: update has a target or it has nothing to do.
    /// The body is either the pattern itself or the same { eventType, pattern } envelope POST takes
    /// - a "pattern" that is an object marks the envelope, since on a bare pattern that field is the
    /// pattern type name.
    /// </summary>
    [HttpPut("{eventType}")]
    public async Task UpdatePattern(string eventType)
    {
        var context = HttpContext;

        try
        {
            if (string.IsNullOrWhiteSpace(eventType))
            {
                await WriteJsonAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    new { error = "Event type is required" });
                return;
            }

            var json = await BoundedRequestReader.ReadOrRespondAsync(context, "Request body is empty");
            if (json == null)
            {
                return;
            }

            if (!BoundedRequestReader.TryParseDocument(json, out var body) ||
                body.ValueKind != JsonValueKind.Object)
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest, new { error = "Invalid JSON format" });
                return;
            }

            var patternElement = body;

            if (body.TryGetProperty("pattern", out var envelope) && envelope.ValueKind == JsonValueKind.Object)
            {
                patternElement = envelope;

                // An envelope naming a different event would edit something other than the event in
                // the route. Refuse it rather than guessing which of the two the caller meant.
                if (body.TryGetProperty("eventType", out var bodyEventType) &&
                    bodyEventType.ValueKind == JsonValueKind.String &&
                    !string.Equals(bodyEventType.GetString(), eventType, StringComparison.Ordinal))
                {
                    await WriteJsonAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        new { error = "The eventType in the body must match the one in the URL" });
                    return;
                }
            }

            if (!TryReadPattern(patternElement, out var pattern, out var patternErrors))
            {
                await WriteJsonAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    new { error = "Invalid pattern definition", errors = patternErrors });
                return;
            }

            var change = _eventMapping.UpdateEventMapping(eventType, pattern!, ReadEnabled(body));
            if (!change.IsApplied)
            {
                await WriteChangeFailureAsync(context, change);
                return;
            }

            var stored = _eventMapping.GetEventMapping(eventType);
            if (stored == null)
            {
                _logger.LogError("Pattern for {EventType} was updated but could not be read back", eventType);
                await ApiError.WriteAsync(context, 500, "The pattern was not stored");
                return;
            }

            _logger.LogInformation("Updated the pattern mapping for event: {EventType}", eventType);

            await WriteJsonAsync(context, StatusCodes.Status200OK, new
            {
                success = true,
                message = $"Pattern for {eventType} updated successfully",
                eventType,
                mapping = DescribeMapping(stored)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating pattern");
            await ApiError.WriteAsync(context, 500, "Failed to update the pattern");
        }
    }

    /// <summary>
    /// Unmaps an event and writes the mappings out. An event with no mapping answers 404 - deleting
    /// nothing is not a success.
    /// </summary>
    [HttpDelete("{eventType}")]
    public async Task DeletePattern(string eventType)
    {
        var context = HttpContext;

        try
        {
            if (string.IsNullOrWhiteSpace(eventType))
            {
                await WriteJsonAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    new { error = "Event type is required" });
                return;
            }

            var change = _eventMapping.RemoveEventMapping(eventType);
            if (!change.IsApplied)
            {
                await WriteChangeFailureAsync(context, change);
                return;
            }

            // Read back: the event has to be gone from the live mappings before the response says so.
            if (_eventMapping.GetEventMapping(eventType) != null)
            {
                _logger.LogError("Pattern for {EventType} was deleted but is still mapped", eventType);
                await ApiError.WriteAsync(context, 500, "The pattern was not removed");
                return;
            }

            _logger.LogInformation("Deleted the pattern mapping for event: {EventType}", eventType);

            await WriteJsonAsync(context, StatusCodes.Status200OK, new
            {
                success = true,
                message = $"Pattern for {eventType} deleted successfully",
                eventType,
                remainingPatterns = _eventMapping.GetEventMappings().Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting pattern");
            await ApiError.WriteAsync(context, 500, "Failed to delete the pattern");
        }
    }

    [HttpPost("{eventType}/test")]
    public async Task TestPattern(string eventType)
    {
        var context = HttpContext;

        try
        {
            if (string.IsNullOrEmpty(eventType))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Event type is required" }));
                return;
            }

            _logger.LogInformation("Testing pattern for event: {EventType}", eventType);

            // Check if this is a custom pattern test (with parameters in body)
            HapticPattern? patternToTest = null;
            var testEvent = new JournalEvent
            {
                Event = eventType,
                Timestamp = DateTime.UtcNow,
                StarSystem = "Test System",
                Health = 0.75, // 75% health for damage testing
                StationName = "Test Station"
            };

            // Check for custom pattern parameters in request body. No body is allowed here: it means
            // "play the stored pattern for this event".
            var json = await BoundedRequestReader.ReadOrRespondAsync(context, emptyBodyError: null);
            if (json == null)
            {
                return;
            }

            if (json.Length > 0 &&
                BoundedRequestReader.TryDeserialize<Dictionary<string, object>>(json, out var customParams) &&
                customParams != null)
            {
                patternToTest = CreateCustomTestPattern(eventType, customParams);

                var limitErrors = PatternLimitsGuard.Validate(patternToTest, $"Event '{eventType}'");
                if (limitErrors.Count > 0)
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        error = "Pattern exceeds the accepted limits",
                        errors = limitErrors
                    }));
                    return;
                }
            }

            // If no custom pattern, play the one currently mapped to this event type
            if (patternToTest == null)
            {
                var eventMapping = _eventMapping.GetEventMapping(eventType);
                if (eventMapping == null)
                {
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        error = $"Pattern not found for event type: {eventType}"
                    }));
                    return;
                }
                patternToTest = eventMapping.Pattern;
            }

            // Test the pattern
            await _audioEngine.PlayHapticPattern(patternToTest, testEvent);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new 
            { 
                success = true, 
                message = $"Pattern for {eventType} played successfully",
                eventType = eventType,
                pattern = new
                {
                    Name = patternToTest.Name,
                    Duration = patternToTest.Duration,
                    Frequency = patternToTest.Frequency,
                    Intensity = patternToTest.Intensity,
                    FadeIn = patternToTest.FadeIn,
                    FadeOut = patternToTest.FadeOut,
                    IntensityCurve = patternToTest.IntensityCurve.ToString()
                }
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing pattern for event: {EventType}", eventType);
            await ApiError.WriteAsync(context, 500, "Failed to test the pattern");
        }
    }

    [HttpPost("test/custom")]
    public async Task TestCustomPattern()
    {
        var context = HttpContext;

        try
        {
            var json = await BoundedRequestReader.ReadOrRespondAsync(context, "Pattern parameters are required");
            if (json == null)
            {
                return;
            }

            if (!BoundedRequestReader.TryDeserialize<Dictionary<string, object>>(json, out var patternParams) ||
                patternParams == null)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Invalid pattern parameters" }));
                return;
            }

            var testPattern = CreateCustomTestPattern("CustomTest", patternParams);

            var limitErrors = PatternLimitsGuard.Validate(testPattern, "Custom pattern");
            if (limitErrors.Count > 0)
            {
                context.Response.StatusCode = 400;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    error = "Pattern exceeds the accepted limits",
                    errors = limitErrors
                }));
                return;
            }

            var testEvent = new JournalEvent
            {
                Event = "CustomTest",
                Timestamp = DateTime.UtcNow,
                StarSystem = "Test System",
                Health = 0.75
            };

            await _audioEngine.PlayHapticPattern(testPattern, testEvent);
            
            _logger.LogInformation("Custom pattern tested with parameters: {Parameters}", json);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new 
            { 
                success = true, 
                message = "Custom pattern played successfully",
                pattern = new
                {
                    Name = testPattern.Name,
                    Duration = testPattern.Duration,
                    Frequency = testPattern.Frequency,
                    Intensity = testPattern.Intensity,
                    FadeIn = testPattern.FadeIn,
                    FadeOut = testPattern.FadeOut,
                    IntensityCurve = testPattern.IntensityCurve.ToString()
                }
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing custom pattern");
            await ApiError.WriteAsync(context, 500, "Failed to test the custom pattern");
        }
    }
    
    /// <summary>
    /// One mapping in the shape every response here uses, so the list, a create, an update and a
    /// read back all describe a pattern the same way.
    /// </summary>
    private static object DescribeMapping(EventMapping mapping) => new
    {
        EventType = mapping.EventType,
        Enabled = mapping.Enabled,
        Pattern = new
        {
            Name = mapping.Pattern.Name,
            PatternType = mapping.Pattern.Pattern.ToString(),
            Frequency = mapping.Pattern.Frequency,
            Duration = mapping.Pattern.Duration,
            Intensity = mapping.Pattern.Intensity,
            FadeIn = mapping.Pattern.FadeIn,
            FadeOut = mapping.Pattern.FadeOut,
            IntensityCurve = mapping.Pattern.IntensityCurve.ToString(),
            EnableVoiceAnnouncement = mapping.Pattern.EnableVoiceAnnouncement,
            VoiceMessage = mapping.Pattern.VoiceMessage,
            EnableAudioCue = mapping.Pattern.EnableAudioCue,
            AudioCueFile = mapping.Pattern.AudioCueFile,
            IntensityFromDamage = mapping.Pattern.IntensityFromDamage,
            MaxIntensity = mapping.Pattern.MaxIntensity,
            MinIntensity = mapping.Pattern.MinIntensity,
            ChainedPatterns = mapping.Pattern.ChainedPatterns,
            Conditions = mapping.Pattern.Conditions,
            Layers = mapping.Pattern.Layers?.Select(l => new
            {
                Waveform = l.Waveform.ToString(),
                Frequency = l.Frequency,
                Amplitude = l.Amplitude,
                Curve = l.Curve.ToString(),
                PhaseOffset = l.PhaseOffset
            }),
            CustomCurvePoints = mapping.Pattern.CustomCurvePoints?.Select(p => new
            {
                Time = p.Time,
                Intensity = p.Intensity
            })
        }
    };

    /// <summary>
    /// Deserializes a pattern from request JSON and holds it to the same content limits as an
    /// imported one. False means <paramref name="errors"/> says what is wrong with it, and nothing
    /// has been stored.
    /// </summary>
    private static bool TryReadPattern(
        JsonElement element,
        out HapticPattern? pattern,
        out IReadOnlyList<string> errors)
    {
        pattern = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            errors = new[] { "A pattern must be a JSON object" };
            return false;
        }

        try
        {
            pattern = element.Deserialize<HapticPattern>(PatternJson);
        }
        catch (JsonException)
        {
            errors = new[] { "The pattern could not be read" };
            return false;
        }

        if (pattern == null)
        {
            errors = new[] { "The pattern could not be read" };
            return false;
        }

        var limitErrors = PatternLimitsGuard.Validate(pattern);
        if (limitErrors.Count > 0)
        {
            pattern = null;
            errors = limitErrors;
            return false;
        }

        errors = Array.Empty<string>();
        return true;
    }

    /// <summary>The optional enabled flag on a request body; null when the caller did not send one.</summary>
    private static bool? ReadEnabled(JsonElement body) =>
        body.TryGetProperty("enabled", out var enabled) &&
        enabled.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? enabled.GetBoolean()
            : null;

    /// <summary>The one place a refused mapping edit becomes a status code.</summary>
    private static Task WriteChangeFailureAsync(HttpContext context, EventMappingChange change) =>
        ApiError.WriteAsync(
            context,
            change.Status switch
            {
                EventMappingChangeStatus.Conflict => StatusCodes.Status409Conflict,
                EventMappingChangeStatus.NotFound => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status500InternalServerError
            },
            change.Error ?? "The pattern could not be changed");

    private static Task WriteJsonAsync(HttpContext context, int statusCode, object payload)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }

    private HapticPattern CreateCustomTestPattern(string eventType, Dictionary<string, object> parameters)
    {
        var pattern = new HapticPattern
        {
            Name = $"Test_{eventType}_{DateTime.Now:HHmmss}",
            Pattern = PatternType.SharpPulse,
            Frequency = 40,
            Duration = 1000,
            Intensity = 80,
            FadeIn = 50,
            FadeOut = 50,
            IntensityCurve = IntensityCurve.Linear,
            EnableVoiceAnnouncement = false,
            EnableAudioCue = false,
            IntensityFromDamage = false,
            MaxIntensity = 100,
            MinIntensity = 10,
            ChainedPatterns = new List<string>(),
            Conditions = new Dictionary<string, object>(),
            Layers = new List<PatternLayer>(),
            CustomCurvePoints = new List<CurvePoint>()
        };

        // Apply custom parameters
        if (parameters.ContainsKey("frequency") && double.TryParse(parameters["frequency"].ToString(), out double freq))
            pattern.Frequency = (int)Math.Max(20, Math.Min(80, freq));

        if (parameters.ContainsKey("duration") && int.TryParse(parameters["duration"].ToString(), out int dur))
            pattern.Duration = Math.Max(100, Math.Min(10000, dur));

        if (parameters.ContainsKey("intensity") && int.TryParse(parameters["intensity"].ToString(), out int intensity))
            pattern.Intensity = Math.Max(1, Math.Min(100, intensity));

        if (parameters.ContainsKey("fadeIn") && int.TryParse(parameters["fadeIn"].ToString(), out int fadeIn))
            pattern.FadeIn = Math.Max(0, Math.Min(5000, fadeIn));

        if (parameters.ContainsKey("fadeOut") && int.TryParse(parameters["fadeOut"].ToString(), out int fadeOut))
            pattern.FadeOut = Math.Max(0, Math.Min(5000, fadeOut));

        if (parameters.ContainsKey("patternType") && Enum.TryParse<PatternType>(parameters["patternType"].ToString(), out PatternType patternType))
            pattern.Pattern = patternType;

        if (parameters.ContainsKey("intensityCurve") && Enum.TryParse<IntensityCurve>(parameters["intensityCurve"].ToString(), out IntensityCurve curve))
            pattern.IntensityCurve = curve;

        // Handle multi-layer patterns
        if (parameters.ContainsKey("layers") && parameters["layers"] is JsonElement layersElement && layersElement.ValueKind == JsonValueKind.Array)
        {
            pattern.Pattern = PatternType.MultiLayer;
            pattern.Layers = new List<PatternLayer>();

            foreach (var layerElement in layersElement.EnumerateArray())
            {
                if (layerElement.TryGetProperty("waveform", out var waveformProp) && Enum.TryParse<WaveformType>(waveformProp.GetString(), out WaveformType waveform) &&
                    layerElement.TryGetProperty("frequency", out var freqProp) && freqProp.TryGetDouble(out double layerFreq) &&
                    layerElement.TryGetProperty("amplitude", out var ampProp) && ampProp.TryGetDouble(out double amplitude))
                {
                    var layer = new PatternLayer
                    {
                        Waveform = waveform,
                        Frequency = (int)layerFreq,
                        Amplitude = (float)amplitude,
                        Curve = IntensityCurve.Linear,
                        PhaseOffset = 0
                    };

                    if (layerElement.TryGetProperty("curve", out var curveProp) && Enum.TryParse<IntensityCurve>(curveProp.GetString(), out IntensityCurve layerCurve))
                        layer.Curve = layerCurve;

                    if (layerElement.TryGetProperty("phaseOffset", out var phaseProp) && phaseProp.TryGetDouble(out double phase))
                        layer.PhaseOffset = (int)phase;

                    pattern.Layers.Add(layer);
                }
            }
        }

        // Handle custom curve points
        if (parameters.ContainsKey("customCurvePoints") && parameters["customCurvePoints"] is JsonElement curveElement && curveElement.ValueKind == JsonValueKind.Array)
        {
            pattern.IntensityCurve = IntensityCurve.Custom;
            pattern.CustomCurvePoints = new List<CurvePoint>();

            foreach (var pointElement in curveElement.EnumerateArray())
            {
                if (pointElement.TryGetProperty("time", out var timeProp) && timeProp.TryGetDouble(out double time) &&
                    pointElement.TryGetProperty("intensity", out var intensityProp) && intensityProp.TryGetDouble(out double pointIntensity))
                {
                    pattern.CustomCurvePoints.Add(new CurvePoint
                    {
                        Time = (float)time,
                        Intensity = (float)pointIntensity
                    });
                }
            }
        }

        return pattern;
    }
}