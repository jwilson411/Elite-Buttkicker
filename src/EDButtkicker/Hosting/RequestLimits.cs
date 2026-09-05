using System.Text.Json;

namespace EDButtkicker.Hosting;

/// <summary>
/// Every bound the loopback API applies to what a caller sends: the request body size Kestrel and
/// the handlers agree on, the JSON shape limits, and the content limits on an imported pattern.
/// They live in one place so the server, the handlers and the tests all mean the same numbers.
/// </summary>
public static class RequestLimits
{
    /// <summary>
    /// The one body cap. This is a JSON configuration API for a single machine: a pattern pack that
    /// does not fit in a mebibyte is not a pattern pack, it is an attempt to fill memory or disk.
    /// </summary>
    public const long MaxRequestBodyBytes = 1_048_576;

    /// <summary>Nesting depth accepted while parsing request JSON.</summary>
    public const int MaxJsonDepth = 32;

    /// <summary>Characters accepted in a name, message or description.</summary>
    public const int MaxStringLength = 4096;

    /// <summary>Characters accepted in anything that names a file.</summary>
    public const int MaxPathLength = 1024;

    public const int MaxShipsPerPack = 128;
    public const int MaxEventsPerShip = 128;
    public const int MaxTags = 64;
    public const int MaxChainedPatterns = 32;
    public const int MaxPatternLayers = 8;
    public const int MaxCurvePoints = 256;
    public const int MaxConditions = 64;

    /// <summary>Longest single haptic pattern, in milliseconds.</summary>
    public const int MaxPatternDurationMs = 10_000;

    /// <summary>Stable body-too-large message, so a caller can match on it.</summary>
    public static readonly string BodyTooLargeError =
        $"Request body exceeds the {MaxRequestBodyBytes} byte limit";

    /// <summary>Stable upload-too-large message, so a caller can match on it.</summary>
    public static readonly string UploadTooLargeError =
        $"Uploaded file exceeds the {MaxRequestBodyBytes} byte limit";

    /// <summary>Deserialization options for request JSON: depth capped, names as the UI sends them.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = MaxJsonDepth,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>As <see cref="Json"/>, for the endpoints whose DTOs are camelCase on the wire.</summary>
    public static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        MaxDepth = MaxJsonDepth,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Document parsing options for request JSON.</summary>
    public static readonly JsonDocumentOptions Document = new()
    {
        MaxDepth = MaxJsonDepth
    };
}
