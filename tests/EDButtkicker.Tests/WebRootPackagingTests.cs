using System.Net;
using EDButtkicker.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The static file root used to be resolved with a fallback: if no wwwroot could be found,
/// <c>ResolveWebRootPath</c> handed the application's own program directory to the static file
/// middleware, and a bad install quietly published the binaries and configuration sitting next to
/// the executable over HTTP. These tests pin the replacement rule from both sides - a directory is
/// only the web root if it actually carries the packaged assets, and a path that resolves to nothing
/// fails startup instead of serving something else.
/// </summary>
public class WebRootPackagingTests : IClassFixture<WebUiTestServerFixture>
{
    private readonly WebUiTestServerFixture _fixture;

    public WebRootPackagingTests(WebUiTestServerFixture fixture) => _fixture = fixture;

    /// <summary>The packaged directory still serves what the pages ask for, vendored fonts included.</summary>
    [Theory]
    [InlineData("/index.html")]
    [InlineData("/css/styles.css")]
    [InlineData("/vendor/fontawesome/css/fontawesome.min.css")]
    [InlineData("/vendor/fontawesome/css/solid.min.css")]
    [InlineData("/vendor/fontawesome/webfonts/fa-solid-900.woff2")]
    public async Task PackagedAsset_IsServedFromTheWebRoot(string path)
    {
        var response = await _fixture.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// The files the old fallback would have exposed. Each one exists next to the test assembly -
    /// asserted first, so the test cannot pass by asking for something that was never there - and
    /// none of them is reachable through the pipeline: the static file root is wwwroot, and the
    /// single-page fallback does not answer a request that names a file.
    /// </summary>
    [Theory]
    [InlineData("EDButtkicker.dll")]
    [InlineData("EDButtkicker.runtimeconfig.json")]
    [InlineData("EDButtkicker.deps.json")]
    public async Task FileNextToTheApplication_IsNotServed(string fileName)
    {
        var onDisk = Path.Combine(AppContext.BaseDirectory, fileName);
        Assert.True(File.Exists(onDisk), $"{fileName} is not in {AppContext.BaseDirectory}; nothing was proven");

        var response = await _fixture.Client.GetAsync("/" + fileName);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(
            new FileInfo(onDisk).Length,
            (await response.Content.ReadAsByteArrayAsync()).LongLength);
    }

    /// <summary>A name the application would carry in a real install, absent from wwwroot.</summary>
    [Fact]
    public async Task RequestForAppSettings_IsNotFound()
    {
        var response = await _fixture.Client.GetAsync("/appsettings.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>A deep link into the UI is still the page, which is what the fallback exists for.</summary>
    [Fact]
    public async Task DeepLinkWithoutAFileName_StillGetsThePage()
    {
        var response = await _fixture.Client.GetAsync("/patterns");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public void ResolveWebRootPath_ReturnsAPackagedWebRootAndNotTheProgramDirectory()
    {
        var resolved = WebUiConfiguration.ResolveWebRootPath(NullLogger.Instance);

        Assert.True(WebUiConfiguration.IsPackagedWebRoot(resolved));
        Assert.True(File.Exists(Path.Combine(resolved, "index.html")));

        // Whatever it found, it is not the directory the assemblies live in.
        Assert.False(File.Exists(Path.Combine(resolved, "EDButtkicker.dll")));
        Assert.NotEqual(
            Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar),
            resolved.TrimEnd(Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// The program directory is the one thing the old code fell back to, and it can never qualify:
    /// resolution only ever returns a directory this predicate accepts, so proving the predicate
    /// rejects it proves the fallback is gone.
    /// </summary>
    [Fact]
    public void IsPackagedWebRoot_RejectsTheProgramDirectoryAndAnythingThatIsNotTheWebRoot()
    {
        using var programDirectory = FakeProgramDirectory();
        using var empty = new TempDirectory("edbk-webroot-empty");

        Assert.False(WebUiConfiguration.IsPackagedWebRoot(AppContext.BaseDirectory));
        Assert.False(WebUiConfiguration.IsPackagedWebRoot(programDirectory.Path));
        Assert.False(WebUiConfiguration.IsPackagedWebRoot(empty.Path));
        Assert.False(WebUiConfiguration.IsPackagedWebRoot(Path.Combine(empty.Path, "nope")));
        Assert.False(WebUiConfiguration.IsPackagedWebRoot(null));

        Assert.True(WebUiConfiguration.IsPackagedWebRoot(
            WebUiConfiguration.ResolveWebRootPath(NullLogger.Instance)));
    }

    /// <summary>
    /// A partial deploy: the wwwroot directory is there but its contents are not. It is not the
    /// packaged web root, so it is not served.
    /// </summary>
    [Fact]
    public void ResolveWebRootPath_RejectsAnEmptyWwwrootDirectory()
    {
        using var temp = new TempDirectory("edbk-webroot-partial");
        var hollow = Path.Combine(temp.Path, "wwwroot");
        Directory.CreateDirectory(hollow);

        Assert.Throws<DirectoryNotFoundException>(
            () => WebUiConfiguration.ResolveWebRootPath(NullLogger.Instance, new[] { hollow }));
    }

    /// <summary>
    /// Nothing resolves: startup fails, and the message names what was looked for and where, because
    /// the operator reading it has to fix an install rather than guess at one.
    /// </summary>
    [Fact]
    public void ResolveWebRootPath_ThrowsWhenNoCandidateIsAPackagedWebRoot()
    {
        using var programDirectory = FakeProgramDirectory();

        var error = Assert.Throws<DirectoryNotFoundException>(() => WebUiConfiguration.ResolveWebRootPath(
            NullLogger.Instance,
            new[] { Path.Combine(programDirectory.Path, "wwwroot"), programDirectory.Path }));

        Assert.Contains("wwwroot", error.Message, StringComparison.Ordinal);
        Assert.Contains("index.html", error.Message, StringComparison.Ordinal);
        Assert.Contains(programDirectory.Path, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A candidate that is not the web root is skipped rather than accepted, even when it exists and
    /// is listed first - which is exactly the order a bad install presents them in.
    /// </summary>
    [Fact]
    public void ResolveWebRootPath_SkipsADirectoryThatIsNotThePackagedWebRoot()
    {
        using var programDirectory = FakeProgramDirectory();
        var packaged = WebUiConfiguration.ResolveWebRootPath(NullLogger.Instance);

        var resolved = WebUiConfiguration.ResolveWebRootPath(
            NullLogger.Instance,
            new[] { programDirectory.Path, packaged });

        Assert.Equal(packaged, resolved);
    }

    /// <summary>
    /// Font Awesome used to come from cdnjs with no integrity hash, so a compromised CDN chose the
    /// stylesheet and the font the UI loaded. Every stylesheet and script a page pulls in is now a
    /// file in this build - which is also what lets the Content-Security-Policy name no third-party
    /// origin at all.
    /// </summary>
    [Theory]
    [InlineData("/index.html")]
    [InlineData("/pattern-editor.html")]
    [InlineData("/pattern-conflicts.html")]
    public async Task Page_LoadsEveryStylesheetAndScriptFromThisBuild(string path)
    {
        var body = await _fixture.Client.GetStringAsync(path);

        var remote = System.Text.RegularExpressions.Regex.Matches(
            body,
            """<(?:link|script)\b[^>]*\b(?:href|src)\s*=\s*["'](?<url>(?:https?:)?//[^"']*)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        Assert.True(
            remote.Count == 0,
            $"{path} loads {string.Join(", ", remote.Select(m => m.Groups["url"].Value))} from another origin");

        // And the icons the page uses are actually there to load.
        Assert.Contains("vendor/fontawesome/css/solid.min.css", body, StringComparison.Ordinal);
    }

    /// <summary>What an application directory holds, and what must never be served from one.</summary>
    private static TempDirectory FakeProgramDirectory()
    {
        var directory = new TempDirectory("edbk-program-dir");

        File.WriteAllText(directory.File("appsettings.json"), """{"Audio":{"DeviceName":"secret"}}""");
        File.WriteAllText(directory.File("EDButtkicker.dll"), "MZ not really an assembly");

        return directory;
    }
}
