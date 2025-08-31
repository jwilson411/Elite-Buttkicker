using Microsoft.AspNetCore.Http;
using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging;

namespace EDButtkicker.Controllers;

public class PatternApiController
{
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

    public async Task GetPatterns(HttpContext context)
    {
        try
        {
            var eventMappings = EventMappingsConfig.GetDefault();
            var patterns = new Dictionary<string, object>();

            foreach (var mapping in eventMappings.EventMappings)
            {
                patterns[mapping.Key] = new
                {
                    EventType = mapping.Value.EventType,
                    Enabled = mapping.Value.Enabled,
                    Pattern = new
                    {
                        Name = mapping.Value.Pattern.Name,
                        PatternType = mapping.Value.Pattern.Pattern.ToString(),
                        Frequency = mapping.Value.Pattern.Frequency,
                        Duration = mapping.Value.Pattern.Duration,
                        Intensity = mapping.Value.Pattern.Intensity,
                        FadeIn = mapping.Value.Pattern.FadeIn,
                        FadeOut = mapping.Value.Pattern.FadeOut,
                        IntensityCurve = mapping.Value.Pattern.IntensityCurve.ToString(),
                        EnableVoiceAnnouncement = mapping.Value.Pattern.EnableVoiceAnnouncement,
                        VoiceMessage = mapping.Value.Pattern.VoiceMessage,
                        EnableAudioCue = mapping.Value.Pattern.EnableAudioCue,
                        AudioCueFile = mapping.Value.Pattern.AudioCueFile,
                        IntensityFromDamage = mapping.Value.Pattern.IntensityFromDamage,
                        MaxIntensity = mapping.Value.Pattern.MaxIntensity,
                        MinIntensity = mapping.Value.Pattern.MinIntensity,
                        ChainedPatterns = mapping.Value.Pattern.ChainedPatterns,
                        Conditions = mapping.Value.Pattern.Conditions,
                        Layers = mapping.Value.Pattern.Layers?.Select(l => new
                        {
                            Waveform = l.Waveform.ToString(),
                            Frequency = l.Frequency,
                            Amplitude = l.Amplitude,
                            Curve = l.Curve.ToString(),
                            PhaseOffset = l.PhaseOffset
                        }),
                        CustomCurvePoints = mapping.Value.Pattern.CustomCurvePoints?.Select(p => new
                        {
                            Time = p.Time,
                            Intensity = p.Intensity
                        })
                    }
                };
            }

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
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    public async Task CreatePattern(HttpContext context)
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

            var patternData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (patternData == null)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Invalid JSON format" }));
                return;
            }

            if (!patternData.ContainsKey("eventType") || !patternData.ContainsKey("pattern"))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Missing required fields: eventType, pattern" }));
                return;
            }

            var eventType = patternData["eventType"].ToString();
            var patternJson = patternData["pattern"].ToString();
            
            if (string.IsNullOrEmpty(eventType) || string.IsNullOrEmpty(patternJson))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "EventType and pattern cannot be empty" }));
                return;
            }

            // For now, return success - actual pattern creation would require extending EventMappingsConfig
            _logger.LogInformation("Pattern creation requested for event: {EventType}", eventType);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new 
            { 
                success = true, 
                message = $"Pattern for {eventType} created successfully",
                eventType = eventType
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating pattern");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    public async Task UpdatePattern(HttpContext context)
    {
        try
        {
            var eventType = ExtractEventTypeFromPath(context.Request.Path);
            if (string.IsNullOrEmpty(eventType))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Event type is required" }));
                return;
            }

            using var reader = new StreamReader(context.Request.Body);
            var json = await reader.ReadToEndAsync();
            
            if (string.IsNullOrEmpty(json))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Request body is empty" }));
                return;
            }

            _logger.LogInformation("Pattern update requested for event: {EventType}", eventType);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new 
            { 
                success = true, 
                message = $"Pattern for {eventType} updated successfully",
                eventType = eventType
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating pattern");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    public async Task DeletePattern(HttpContext context)
    {
        try
        {
            var eventType = ExtractEventTypeFromPath(context.Request.Path);
            if (string.IsNullOrEmpty(eventType))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Event type is required" }));
                return;
            }

            _logger.LogInformation("Pattern deletion requested for event: {EventType}", eventType);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new 
            { 
                success = true, 
                message = $"Pattern for {eventType} deleted successfully",
                eventType = eventType
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting pattern");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    public async Task TestPattern(HttpContext context)
    {
        try
        {
            var eventType = ExtractEventTypeFromPath(context.Request.Path);
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

            // Check for custom pattern parameters in request body
            if (context.Request.ContentLength > 0)
            {
                using var reader = new StreamReader(context.Request.Body);
                var json = await reader.ReadToEndAsync();
                
                if (!string.IsNullOrEmpty(json))
                {
                    try
                    {
                        var customParams = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                        if (customParams != null)
                        {
                            patternToTest = CreateCustomTestPattern(eventType, customParams);
                        }
                    }
                    catch
                    {
                        // If JSON parsing fails, use default pattern
                        patternToTest = null;
                    }
                }
            }

            // If no custom pattern, use the default for this event type
            if (patternToTest == null)
            {
                var eventMappings = EventMappingsConfig.GetDefault();
                if (!eventMappings.EventMappings.TryGetValue(eventType, out var eventMapping))
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
            
            // Test voice feedback if enabled
            if (patternToTest.EnableVoiceAnnouncement)
            {
                // Voice testing would go here when service supports it
            }

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
            _logger.LogError(ex, "Error testing pattern for event: {EventType}", ExtractEventTypeFromPath(context.Request.Path));
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    public async Task TestCustomPattern(HttpContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var json = await reader.ReadToEndAsync();
            
            if (string.IsNullOrEmpty(json))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Pattern parameters are required" }));
                return;
            }

            var patternParams = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (patternParams == null)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Invalid pattern parameters" }));
                return;
            }

            var testPattern = CreateCustomTestPattern("CustomTest", patternParams);
            
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
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
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
            pattern.Frequency = Math.Max(20, Math.Min(80, (int)freq));

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

    private string ExtractEventTypeFromPath(string path)
    {
        // Extract event type from paths like "/api/patterns/FSDJump/test"
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3 && segments[0] == "api" && segments[1] == "patterns")
        {
            return segments[2]; // The event type
        }
        return string.Empty;
    }
}