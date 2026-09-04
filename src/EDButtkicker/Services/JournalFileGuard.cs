namespace EDButtkicker.Services;

/// <summary>
/// Single place where a caller-supplied journal file name is turned into a filesystem path.
/// Replay may only ever address a file the server itself enumerated, so a name is resolved against
/// <see cref="JournalGlob"/> inside the configured journal folder and then matched against the
/// live enumeration - a name that is not currently one of those files has no path at all.
/// </summary>
public static class JournalFileGuard
{
    /// <summary>The Elite Dangerous journal file glob. Never caller-controlled.</summary>
    public const string JournalGlob = "Journal.*.log";

    private const string NamePrefix = "Journal.";
    private const string NameSuffix = ".log";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Returns the sanitized journal file name, or null when the value carries any directory
    /// component or percent-encoded variant of one, or is not shaped like a journal file name.
    /// Shape only: existence is <see cref="Resolve"/>'s job.
    /// </summary>
    public static string? SanitizeFileName(string? name)
    {
        // The pattern guard already rejects every separator, drive/stream qualifier, "."/"..",
        // control character and percent-encoded variant thereof; journal names add the glob.
        var fileName = PatternPathGuard.SanitizeFileName(name);
        if (fileName == null)
        {
            return null;
        }

        // "Journal." + at least one character + ".log", which is what the glob matches - so
        // "Journal.log" and "status.json" are out.
        if (fileName.Length <= NamePrefix.Length + NameSuffix.Length ||
            !fileName.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(NameSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return fileName;
    }

    /// <summary>
    /// Resolves <paramref name="requestedName"/> to a full path inside
    /// <paramref name="journalDirectory"/>, but only when that path is one of the journal files the
    /// directory currently holds. Returns null otherwise, and never touches the requested file.
    /// </summary>
    public static string? Resolve(string? journalDirectory, string? requestedName)
    {
        if (string.IsNullOrWhiteSpace(journalDirectory) || !Directory.Exists(journalDirectory))
        {
            return null;
        }

        var safeName = SanitizeFileName(requestedName);
        if (safeName == null)
        {
            return null;
        }

        string rootFull;
        string candidate;
        try
        {
            rootFull = Path.GetFullPath(journalDirectory);
            candidate = Path.GetFullPath(Path.Combine(rootFull, safeName));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var rootPrefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        // The trailing separator is what keeps a sibling "…-evil" folder from counting as inside.
        if (!candidate.StartsWith(rootPrefix, PathComparison))
        {
            return null;
        }

        // The allow-list: the resolved path has to be one of the files the server would have
        // offered, which is the same enumeration the journal status API returns.
        try
        {
            var enumerated = Directory.GetFiles(rootFull, JournalGlob);

            return enumerated.Any(file =>
                string.Equals(Path.GetFullPath(file), candidate, PathComparison))
                ? candidate
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
