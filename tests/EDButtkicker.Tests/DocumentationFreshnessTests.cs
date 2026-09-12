using System.Text.RegularExpressions;
using EDButtkicker.Hosting;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The docs used to name three different ports for one server - 5000 in the root implementation
/// notes, 8080 baked into the UI's own status panel, 47811 in the console - and to tell a new user
/// to answer console prompts that the streamlined startup had already removed. Nothing in the build
/// noticed, because prose is not compiled. These checks are that missing compiler: the live docs are
/// read as files and measured against the one constant the host actually binds, so the next port
/// change fails here until the documentation moves with it.
///
/// Historical notes under <c>docs/archive/</c> are deliberately exempt - they are design history,
/// carry a header saying so, and are allowed to keep describing the world they were written in.
/// </summary>
public class DocumentationFreshnessTests
{
    /// <summary>Ports this project has never listened on, or stopped listening on.</summary>
    private static readonly string[] StalePortAddresses = { "localhost:5000", "localhost:8080" };

    [Fact]
    public void LiveDocumentation_DoesNotNameAPortTheApplicationDoesNotListenOn()
    {
        var offenders = new List<string>();

        foreach (var (relativePath, text) in ReadLiveMarkdown())
        {
            foreach (var stale in StalePortAddresses)
            {
                if (text.Contains(stale, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{relativePath} still mentions {stale}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "The web interface listens on one port, WebUiConfiguration.Port = "
                + $"{WebUiConfiguration.Port}: {string.Join("; ", offenders)}");
    }

    /// <summary>
    /// The application auto-configures and opens its web interface; it asks the user nothing in the
    /// console. Documentation that sends a new user looking for console prompts describes a build
    /// that no longer exists.
    /// </summary>
    [Fact]
    public void LiveDocumentation_DoesNotPromiseAnInteractiveConsoleSetupFlow()
    {
        var offenders = new List<string>();

        foreach (var (relativePath, text) in ReadLiveMarkdown())
        {
            if (Regex.IsMatch(text, @"follow\s+the\s+setup\s+prompts", RegexOptions.IgnoreCase))
            {
                offenders.Add($"{relativePath} tells the user to follow interactive setup prompts");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "First-run configuration happens in the web interface, not in console prompts: "
                + string.Join("; ", offenders));
    }

    /// <summary>
    /// Quick Start is where a new user learns the address to open, so the port has to be reachable
    /// from that section rather than buried somewhere further down the file.
    /// </summary>
    [Fact]
    public void ReadmeQuickStart_NamesThePortTheHostActuallyBinds()
    {
        var readme = ReadRepositoryFile("README.md");
        var quickStart = SectionOf(readme, "Quick Start");

        Assert.False(
            string.IsNullOrWhiteSpace(quickStart),
            "README.md no longer has a 'Quick Start' section for a new user to follow");

        Assert.Contains(
            $"localhost:{WebUiConfiguration.Port}",
            quickStart,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Quick Start should point at the web interface rather than leave the reader to guess what the
    /// application does once it starts.
    /// </summary>
    [Fact]
    public void ReadmeQuickStart_DescribesTheWebInterfaceFirstRunFlow()
    {
        var quickStart = SectionOf(ReadRepositoryFile("README.md"), "Quick Start");

        Assert.Contains("web interface", quickStart, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The archived notes are the exception to every rule above, so they have to say what they are -
    /// otherwise they read as current setup instructions again.
    /// </summary>
    [Fact]
    public void ArchivedDesignNotes_AnnounceThatTheyAreHistorical()
    {
        var archive = Path.Combine(RepositoryRoot(), "docs", "archive");
        Assert.True(Directory.Exists(archive), $"{archive} is missing");

        var notes = Directory.GetFiles(archive, "*.md")
            .Where(path => !Path.GetFileName(path).Equals("README.md", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(notes);

        foreach (var note in notes)
        {
            Assert.True(
                Regex.IsMatch(File.ReadAllText(note), @"[Hh]istorical", RegexOptions.None),
                $"{Path.GetFileName(note)} does not say it is a historical note");
        }
    }

    // ---- Helpers --------------------------------------------------------------------------------

    /// <summary>
    /// Every Markdown file a reader of this repository would treat as current: the whole checkout
    /// minus the archive, minus build output and minus dependencies this project did not write.
    /// </summary>
    private static IEnumerable<(string RelativePath, string Text)> ReadLiveMarkdown()
    {
        var root = RepositoryRoot();
        var archive = Path.Combine(root, "docs", "archive") + Path.DirectorySeparatorChar;
        var skipped = new[] { "node_modules", "bin", "obj", ".git" };
        var found = 0;

        foreach (var path in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            if (path.StartsWith(archive, StringComparison.Ordinal)) continue;

            var relative = Path.GetRelativePath(root, path);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(segment => skipped.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            found++;
            yield return (relative, File.ReadAllText(path));
        }

        Assert.True(found > 0, $"no Markdown files were found under {root}");
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));

    /// <summary>
    /// The text under a top-level <c>##</c> heading, up to the next one - i.e. what a reader sees
    /// when they follow that heading, including its subsections.
    /// </summary>
    private static string SectionOf(string markdown, string heading)
    {
        var match = Regex.Match(
            markdown,
            $@"^##\s+{Regex.Escape(heading)}\s*$(?<body>.*?)(?=^##\s|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline);

        return match.Success ? match.Groups["body"].Value : string.Empty;
    }

    /// <summary>
    /// Walks up from the test binary to the checkout root, anchored by the solution file, so the
    /// test does not depend on the working directory. Same approach as
    /// <see cref="PatternSchemaValidationTests"/>.
    /// </summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EDButtkicker.sln")))
            {
                return directory.FullName;
            }
        }

        Assert.Fail($"Could not find EDButtkicker.sln above {AppContext.BaseDirectory}");
        return string.Empty;
    }
}
