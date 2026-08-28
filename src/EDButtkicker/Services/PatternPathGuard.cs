namespace EDButtkicker.Services;

/// <summary>
/// Single place where caller-supplied pattern file names are turned into filesystem paths.
/// A name is only ever a single file name; the directory always comes from the server.
/// </summary>
public static class PatternPathGuard
{
    private const int MaxDecodePasses = 4;

    /// <summary>Subdirectories the pattern APIs are allowed to address. Never caller-controlled.</summary>
    private static readonly HashSet<string> AllowedSubdirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Custom",
        "Community",
        "Small_Ships",
        "Large_Ships",
        "patterns",
        "imports",
        "exports"
    };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Returns the sanitized single file name, or null when the value carries any directory
    /// component, drive/stream qualifier, or percent-encoded variant of one.
    /// </summary>
    public static string? SanitizeFileName(string? name, bool requireJsonExtension = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var decoded = name;
        for (var pass = 0; pass < MaxDecodePasses; pass++)
        {
            string next;
            try
            {
                next = Uri.UnescapeDataString(decoded);
            }
            catch (UriFormatException)
            {
                return null;
            }

            if (string.Equals(next, decoded, StringComparison.Ordinal))
            {
                break;
            }

            decoded = next;
        }

        if (string.IsNullOrWhiteSpace(decoded))
        {
            return null;
        }

        // '/' and '\' are both rejected on every platform: a Windows-shaped path must not become a
        // legal Linux file name just because the alt separator differs there.
        if (decoded.IndexOf('/') >= 0 ||
            decoded.IndexOf('\\') >= 0 ||
            decoded.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
            decoded.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
        {
            return null;
        }

        if (decoded.IndexOf(':') >= 0)
        {
            return null;
        }

        if (decoded == "." || decoded == "..")
        {
            return null;
        }

        if (decoded.Any(char.IsControl))
        {
            return null;
        }

        if (Path.IsPathRooted(decoded))
        {
            return null;
        }

        var fileName = Path.GetFileName(decoded);
        if (!string.Equals(fileName, decoded, StringComparison.Ordinal))
        {
            return null;
        }

        if (requireJsonExtension && !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return fileName;
    }

    /// <summary>
    /// Resolves <paramref name="fileName"/> inside <paramref name="rootDirectory"/> (optionally one
    /// allow-listed subdirectory deep). Returns null when the name is rejected or the canonical path
    /// would land outside the root.
    /// </summary>
    public static string? ResolveUnderRoot(
        string rootDirectory,
        string? fileName,
        string? relativeSubdirectory = null,
        bool requireJsonExtension = false)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return null;
        }

        var safeName = SanitizeFileName(fileName, requireJsonExtension);
        if (safeName == null)
        {
            return null;
        }

        if (relativeSubdirectory != null && !AllowedSubdirectories.Contains(relativeSubdirectory))
        {
            return null;
        }

        var rootFull = Path.GetFullPath(rootDirectory);
        var combined = relativeSubdirectory == null
            ? Path.Combine(rootFull, safeName)
            : Path.Combine(rootFull, relativeSubdirectory, safeName);

        var candidate = Path.GetFullPath(combined);

        var rootPrefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        // The trailing separator is what keeps "/patterns" from matching "/patterns-evil".
        return candidate.StartsWith(rootPrefix, PathComparison) ? candidate : null;
    }
}
