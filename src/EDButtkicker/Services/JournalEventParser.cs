using System.Text.Json;
using Microsoft.Extensions.Logging;
using EDButtkicker.Models;

namespace EDButtkicker.Services;

/// <summary>
/// Turns one journal line into a <see cref="JournalEvent"/>. Elite writes one JSON object per line,
/// but a line can still be blank, truncated by an editor, or something other than an object - none of
/// which may tear down monitoring, so parsing failures are reported as false rather than thrown.
/// Free of any file or audio dependency so the parse contract can be exercised directly in tests.
/// </summary>
public static class JournalEventParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Parses a single journal line. Returns false (and leaves <paramref name="journalEvent"/> null)
    /// for blank lines, malformed JSON, and JSON that is not an object.
    /// </summary>
    public static bool TryParse(string? line, out JournalEvent? journalEvent, ILogger? logger = null)
    {
        journalEvent = null;

        if (string.IsNullOrWhiteSpace(line))
            return false;

        try
        {
            journalEvent = JsonSerializer.Deserialize<JournalEvent>(line, Options);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning("Failed to parse journal line: {Error}", ex.Message);
            logger?.LogDebug("Problematic line: {Line}", line);
            return false;
        }

        return journalEvent != null;
    }
}
