using System.Reflection;

namespace EDButtkicker.Configuration;

/// <summary>
/// The one place the application answers "which build is this?". The string comes from the
/// assembly the release workflow stamps (<c>dotnet publish -p:Version=...</c>), so a packaged
/// release reports its tag and nothing has to be kept in sync by hand.
///
/// An unstamped local build reports <see cref="DevelopmentVersion"/> - the default
/// <c>&lt;Version&gt;</c> in the project file - so a bug report from a dev build is recognisable
/// as one instead of quietly claiming to be a release.
/// </summary>
public static class BuildVersion
{
    /// <summary>
    /// What a build carries when no release version was passed in. Kept identical to the
    /// <c>&lt;Version&gt;</c> property in <c>EDButtkicker.csproj</c>; if the assembly carries no
    /// version metadata at all, this is what gets reported.
    /// </summary>
    public const string DevelopmentVersion = "0.0.0-dev";

    /// <summary>The running build's version, e.g. <c>1.1.0</c> or <c>0.0.0-dev</c>.</summary>
    public static string Current { get; } = Describe(typeof(BuildVersion).Assembly);

    /// <summary>
    /// Reads the version out of one assembly. The informational version is preferred because it
    /// keeps the full release string ("1.1.0", including any prerelease suffix) that the numeric
    /// <see cref="AssemblyName.Version"/> truncates to four integers.
    /// </summary>
    internal static string Describe(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SourceLink-style builds append "+<commit sha>"; the commit is not what a bug
            // reporter is being asked for.
            var plus = informational.IndexOf('+');
            var trimmed = (plus >= 0 ? informational[..plus] : informational).Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        var assemblyVersion = assembly.GetName().Version;
        return assemblyVersion is null ? DevelopmentVersion : assemblyVersion.ToString();
    }
}
