using System.Reflection;
using EDButtkicker.Configuration;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// CONTRIBUTING.md asks bug reporters to run <c>EDButtkicker.exe --version</c>. These tests run the
/// real entry point with that flag, so the promise is checked against the shipped behaviour rather
/// than against a helper that only the tests call.
///
/// The load-bearing assertion is that the entry point <em>returns</em>: the normal startup path ends
/// in <c>host.RunAsync()</c>, which blocks until shutdown, so a run that completes on its own is a
/// run that never started the journal monitor, the audio services or the web server.
/// </summary>
public class VersionFlagTests
{
    private static readonly Assembly Application = typeof(BuildVersion).Assembly;

    /// <summary>Long enough to absorb a slow CI machine, short enough to fail rather than hang.</summary>
    private static readonly TimeSpan EntryPointBudget = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public async Task VersionFlag_PrintsTheBuildVersionAndExitsWithoutStartingTheApplication(string flag)
    {
        var output = await RunEntryPointAsync(flag);

        Assert.Contains($"EDButtkicker {BuildVersion.Current}", output, StringComparison.Ordinal);

        // Anything the running application prints - it would have printed all of this before
        // reaching the blocking host run.
        Assert.DoesNotContain("is running!", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Web Interface", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Supported Events", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reported string has to come from the assembly the release workflow stamps with
    /// <c>-p:Version=&lt;tag&gt;</c>, otherwise a release would report whatever literal was last
    /// typed into the source.
    /// </summary>
    [Fact]
    public void ReportedVersion_ComesFromTheAssemblysOwnMetadata()
    {
        var informational = Application
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        Assert.False(string.IsNullOrWhiteSpace(informational), "the application assembly carries no version metadata");

        var expected = informational!.Split('+')[0];
        Assert.Equal(expected, BuildVersion.Current);
    }

    /// <summary>
    /// An unstamped build must not look like a release. The project file's default is the explicit
    /// development marker, not the SDK's silent <c>1.0.0</c>, so a bug report from a local build
    /// says so.
    /// </summary>
    [Fact]
    public void UnstampedBuild_ReportsARecognisableDevelopmentVersion()
    {
        var projectFile = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "EDButtkicker", "EDButtkicker.csproj"));

        Assert.Contains($"<Version>{BuildVersion.DevelopmentVersion}</Version>", projectFile, StringComparison.Ordinal);
        Assert.NotEqual("1.0.0", BuildVersion.DevelopmentVersion);
    }

    [Fact]
    public async Task Help_DocumentsTheVersionFlagAlongsideTheOtherOptions()
    {
        var output = await RunEntryPointAsync("--help");

        Assert.Contains("-v, --version", output, StringComparison.Ordinal);
        Assert.Contains("-d, --debug", output, StringComparison.Ordinal);
        Assert.Contains("-h, --help", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The claim in CONTRIBUTING.md is the reason this flag exists; if the file stops asking for it,
    /// or the flag stops answering, one of the two has moved without the other.
    /// </summary>
    [Fact]
    public void Contributing_StillTellsBugReportersToAskTheApplicationForItsVersion()
    {
        var contributing = File.ReadAllText(Path.Combine(RepositoryRoot(), "CONTRIBUTING.md"));

        Assert.Contains("EDButtkicker.exe --version", contributing, StringComparison.Ordinal);
    }

    // ---- Helpers --------------------------------------------------------------------------------

    /// <summary>
    /// Runs the application's real entry point with <paramref name="args"/> and returns everything it
    /// wrote to the console. Fails the test rather than hanging if the run does not finish, which is
    /// what would happen if the flag fell through into normal startup.
    /// </summary>
    private static async Task<string> RunEntryPointAsync(params string[] args)
    {
        var entryPoint = Application.EntryPoint;
        Assert.NotNull(entryPoint);

        var captured = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(captured);

        try
        {
            var run = Task.Run(() => entryPoint!.Invoke(null, new object[] { args }));
            var finished = await Task.WhenAny(run, Task.Delay(EntryPointBudget));

            Assert.True(
                ReferenceEquals(finished, run),
                $"'{string.Join(' ', args)}' did not exit within {EntryPointBudget.TotalSeconds:0} seconds - "
                    + "it reached the blocking application startup instead of printing and returning");

            await run;
        }
        finally
        {
            Console.SetOut(previous);
        }

        return captured.ToString();
    }

    /// <summary>Walks up from the test binary to the checkout root, anchored by the solution file.</summary>
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
