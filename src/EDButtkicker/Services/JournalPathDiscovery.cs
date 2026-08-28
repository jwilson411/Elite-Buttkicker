using EDButtkicker.Configuration;

namespace EDButtkicker.Services;

/// <summary>One folder the wizard offers, together with what is actually in it.</summary>
public sealed record JournalPathCandidate(
    string Path,
    string Source,
    bool Exists,
    int JournalFileCount,
    DateTime? LatestJournalWriteUtc,
    bool IsConfigured,
    bool IsRecommended);

/// <summary>
/// Finds the folders Elite Dangerous writes journals to. The wizard shows what was found and what
/// is in each folder rather than silently assuming the default path exists, which is what the old
/// startup path did.
/// </summary>
public class JournalPathDiscovery
{
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<string> _searchPaths;

    public JournalPathDiscovery(AppSettings settings)
        : this(settings, DefaultSearchPaths())
    {
    }

    /// <summary>Overload that searches explicit folders, so tests can point at a temp directory.</summary>
    public JournalPathDiscovery(AppSettings settings, IEnumerable<string> searchPaths)
    {
        _settings = settings;
        _searchPaths = searchPaths.ToList();
    }

    /// <summary>The usual Elite Dangerous journal locations on a Windows install.</summary>
    public static IReadOnlyList<string> DefaultSearchPaths()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return new[]
        {
            Path.Combine(userProfile, "Saved Games", "Frontier Developments", "Elite Dangerous"),
            // OneDrive's "back up my folders" moves Saved Games without telling the game's tools.
            Path.Combine(userProfile, "OneDrive", "Saved Games", "Frontier Developments", "Elite Dangerous")
        };
    }

    /// <summary>
    /// The configured folder first, then every known location, de-duplicated. The recommendation is
    /// the first folder that actually holds journal files, falling back to the first that exists.
    /// </summary>
    public IReadOnlyList<JournalPathCandidate> Discover()
    {
        var configuredPath = _settings.EliteDangerous.JournalPath;
        var ordered = new List<(string Path, string Source)>();

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            ordered.Add((configuredPath, "configured"));
        }

        foreach (var searchPath in _searchPaths)
        {
            ordered.Add((searchPath, "known-location"));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<JournalPathCandidate>();

        foreach (var (path, source) in ordered)
        {
            var normalized = NormalizeOrNull(path);
            if (normalized == null || !seen.Add(normalized))
            {
                continue;
            }

            var isConfigured = !string.IsNullOrWhiteSpace(configuredPath)
                && string.Equals(normalized, NormalizeOrNull(configuredPath), StringComparison.OrdinalIgnoreCase);

            candidates.Add(Inspect(normalized, source, isConfigured));
        }

        var recommended = candidates.FirstOrDefault(c => c.JournalFileCount > 0)
            ?? candidates.FirstOrDefault(c => c.Exists);

        if (recommended == null)
        {
            return candidates;
        }

        return candidates
            .Select(c => c.Path == recommended.Path ? c with { IsRecommended = true } : c)
            .ToList();
    }

    /// <summary>Expands the placeholder Windows users paste from the game's own documentation.</summary>
    public static string ExpandUserProfile(string path) =>
        path.Contains("%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            ? path.Replace(
                "%USERPROFILE%",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                StringComparison.OrdinalIgnoreCase)
            : path;

    /// <summary>Reads one folder without assuming it exists or can be listed.</summary>
    public static JournalPathCandidate Inspect(string path, string source, bool isConfigured)
    {
        var exists = Directory.Exists(path);
        var count = 0;
        DateTime? latestWriteUtc = null;

        if (exists)
        {
            try
            {
                var files = Directory.GetFiles(path, JournalTailReader.JournalSearchPattern);
                count = files.Length;

                if (count > 0)
                {
                    latestWriteUtc = files.Max(File.GetLastWriteTimeUtc);
                }
            }
            catch (Exception)
            {
                // Unreadable folder (permissions, or it vanished mid-scan): report it as empty
                // rather than failing the whole discovery.
                count = 0;
            }
        }

        return new JournalPathCandidate(path, source, exists, count, latestWriteUtc, isConfigured, IsRecommended: false);
    }

    private static string? NormalizeOrNull(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(ExpandUserProfile(path.Trim())));
        }
        catch (Exception)
        {
            // A malformed configured path is not a candidate, but it must not break discovery.
            return null;
        }
    }
}
