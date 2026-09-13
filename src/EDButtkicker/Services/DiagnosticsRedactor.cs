using System.Text.RegularExpressions;

namespace EDButtkicker.Services;

/// <summary>
/// Makes free text safe to paste into a public issue. Everything that reaches a support bundle goes
/// through here, because the strings a bundle carries - health reasons, audio errors, logged failure
/// messages - are written all over the codebase and routinely contain the reporter's home directory,
/// their Windows account name, or whatever a future call site decided to interpolate.
///
/// The rules are deliberately blunt: a placeholder is always better for triage than a real path, and
/// a maintainer reading <c>[UserProfile]\Saved Games\...</c> learns everything they needed from the
/// original string. Placeholders are bracketed rather than angle-bracketed so a bundle stays readable
/// once it has been through a JSON encoder.
/// </summary>
public sealed class DiagnosticsRedactor
{
    public const string HomePlaceholder = "[UserProfile]";
    public const string AppDataPlaceholder = "[AppData]";
    public const string LocalAppDataPlaceholder = "[LocalAppData]";
    public const string TempPlaceholder = "[Temp]";
    public const string AppDirectoryPlaceholder = "[AppDirectory]";
    public const string UserNamePlaceholder = "[user]";
    public const string MachineNamePlaceholder = "[machine]";
    public const string JournalPayloadPlaceholder = "[journal payload removed]";
    public const string NotSetPlaceholder = "[not set]";

    /// <summary>
    /// Free text a bundle carries is capped: a log message that embedded a whole file is evidence of
    /// nothing, and the reporter has to be able to read what they are about to publish.
    /// </summary>
    public const int MaxTextLength = 600;

    private const string TruncationSuffix = "… (truncated)";

    /// <summary>Windows home directories, including one belonging to another account.</summary>
    private static readonly Regex WindowsHomeDirectory = new(
        @"[A-Za-z]:[\\/]Users[\\/][^\\/\r\n""']+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Linux and macOS home directories.</summary>
    private static readonly Regex UnixHomeDirectory = new(
        @"/(?:home|Users)/[^/\r\n""' ]+",
        RegexOptions.Compiled);

    /// <summary>A quoted JSON property name, which is what makes a brace-delimited span a payload.</summary>
    private static readonly Regex JsonPropertyName = new(
        @"""[^""]+""\s*:",
        RegexOptions.Compiled);

    /// <summary>
    /// Configuration key names whose value must never be copied into a bundle. Config has no secrets
    /// today; this is what keeps that true when someone adds one, because unknown keys are reported
    /// by name only and a name that matches this is not even worth listing as "present".
    /// </summary>
    private static readonly string[] SensitiveKeyFragments =
    {
        "password", "passphrase", "secret", "token", "apikey", "key",
        "credential", "auth", "bearer", "signature", "cookie", "session"
    };

    /// <summary>Longest first, so <c>%AppData%</c> inside the profile wins over the profile itself.</summary>
    private readonly List<(string Value, string Placeholder)> _replacements;

    public DiagnosticsRedactor()
        : this(Array.Empty<string>())
    {
    }

    /// <param name="additionalSensitiveValues">
    /// Literal strings this process knows are identifying and that no placeholder rule would catch
    /// (a configured folder name, for instance). Empty and very short values are ignored: replacing
    /// a two-character string everywhere would shred the text without protecting anything.
    /// </param>
    public DiagnosticsRedactor(IEnumerable<string> additionalSensitiveValues)
    {
        var replacements = new List<(string Value, string Placeholder)>
        {
            (SafeFolder(Environment.SpecialFolder.ApplicationData), AppDataPlaceholder),
            (SafeFolder(Environment.SpecialFolder.LocalApplicationData), LocalAppDataPlaceholder),
            (SafeFolder(Environment.SpecialFolder.UserProfile), HomePlaceholder),
            (Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), TempPlaceholder),
            (AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), AppDirectoryPlaceholder),
            (Environment.UserName, UserNamePlaceholder),
            (Environment.MachineName, MachineNamePlaceholder)
        };

        replacements.AddRange(additionalSensitiveValues.Select(value => (value, UserNamePlaceholder)));

        _replacements = replacements
            .Where(r => r.Value.Length >= 3)
            .OrderByDescending(r => r.Value.Length)
            .ToList();
    }

    /// <summary>
    /// A bundle-safe version of <paramref name="text"/>: no payloads, no absolute paths, no account
    /// or machine names, and short enough to read. <c>null</c> stays <c>null</c> so an absent value
    /// is still visibly absent in the bundle.
    /// </summary>
    public string? Redact(string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (text.Length == 0)
        {
            return text;
        }

        var redacted = RemoveJsonPayloads(text);

        // This machine's own roots first, longest first, so the most specific placeholder wins:
        // on Linux the settings directory sits inside the home directory, and reporting it as
        // "[UserProfile]/.config/..." would lose which folder the app actually uses.
        foreach (var (value, placeholder) in _replacements)
        {
            redacted = redacted.Replace(value, placeholder, StringComparison.OrdinalIgnoreCase);
        }

        // Anything left that still looks like somebody's home directory - another account, a path
        // copied from a different machine, a folder this process never resolves itself.
        redacted = WindowsHomeDirectory.Replace(redacted, HomePlaceholder);
        redacted = UnixHomeDirectory.Replace(redacted, HomePlaceholder);

        return Truncate(redacted);
    }

    /// <summary>
    /// Same rules, plus an explicit marker for "nothing configured" - a bundle that shows an empty
    /// string there reads as a bug in the bundle rather than as an unset setting.
    /// </summary>
    public string RedactPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? NotSetPlaceholder : Redact(path)!;

    /// <summary>
    /// Whether a configuration key names something whose value is a credential. Matching is on the
    /// key's letters and digits only, so <c>api_key</c>, <c>ApiKey</c> and <c>API-KEY</c> are one
    /// key as far as this is concerned.
    /// </summary>
    public static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var normalized = new string(key.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        return SensitiveKeyFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal));
    }

    /// <summary>
    /// Drops the brace-delimited span from the first <c>{</c> to the last <c>}</c> when it looks like
    /// JSON. A journal line that reached a log message is the worst thing a bundle could publish -
    /// it is the reporter's commander name, location and balances in one string - so the whole span
    /// goes, rather than trying to pick identifying fields out of it.
    /// </summary>
    private static string RemoveJsonPayloads(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return text;
        }

        var end = text.LastIndexOf('}');
        if (end <= start)
        {
            return text;
        }

        if (!JsonPropertyName.IsMatch(text.AsSpan(start, end - start + 1).ToString()))
        {
            return text;
        }

        return string.Concat(text.AsSpan(0, start), JournalPayloadPlaceholder, text.AsSpan(end + 1));
    }

    private static string Truncate(string text) =>
        text.Length <= MaxTextLength
            ? text
            : string.Concat(text.AsSpan(0, MaxTextLength), TruncationSuffix);

    /// <summary>
    /// Special folders are not guaranteed to resolve - on Linux several of them are empty strings -
    /// and an empty replacement value would match everywhere.
    /// </summary>
    private static string SafeFolder(Environment.SpecialFolder folder)
    {
        try
        {
            return Environment.GetFolderPath(folder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
