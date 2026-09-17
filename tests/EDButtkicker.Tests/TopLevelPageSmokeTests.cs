using System.Net;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The UI is a set of static pages served ahead of the catch-all route, and a page is only
/// reachable if its file, its stylesheet and its scripts all come back. The catch-all in
/// <c>WebUiConfiguration</c> answers index.html for any non-API path, so a missing page fails as a
/// silently wrong page rather than a 404 - these tests read the body, not just the status code.
/// </summary>
public class TopLevelPageSmokeTests : IClassFixture<WebUiTestServerFixture>
{
    private readonly WebUiTestServerFixture _fixture;

    public TopLevelPageSmokeTests(WebUiTestServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("/", "Elite Dangerous Buttkicker")]
    [InlineData("/index.html", "New Pattern")]
    [InlineData("/pattern-editor.html", "Pattern Editor")]
    [InlineData("/pattern-conflicts.html", "Pattern Conflicts")]
    public async Task TopLevelPage_IsServedAsHtmlAndCarriesItsOwnContent(string path, string expected)
    {
        var response = await _fixture.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET {path} returned {(int)response.StatusCode}");
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(expected, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/css/styles.css")]
    [InlineData("/js/app.js")]
    [InlineData("/js/pattern-conflicts.js")]
    [InlineData("/js/pattern-editor.js")]
    [InlineData("/js/csrf.js")]
    [InlineData("/js/dom.js")]
    public async Task StaticAsset_IsServed(string path)
    {
        var response = await _fixture.Client.GetAsync(path);

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET {path} returned {(int)response.StatusCode}");
    }

    [Fact]
    public async Task PatternConflictsPage_LinksTheStylesheetThatExists()
    {
        // css/style.css has never existed; the page used to ask for it and rendered unstyled.
        var body = await _fixture.Client.GetStringAsync("/pattern-conflicts.html");

        Assert.Contains("css/styles.css", body, StringComparison.Ordinal);
        Assert.DoesNotContain("css/style.css", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndexPage_ReachesTheConflictsPageAndTheEditor()
    {
        var body = await _fixture.Client.GetStringAsync("/index.html");

        Assert.Contains("pattern-conflicts.html", body, StringComparison.Ordinal);
        Assert.Contains("New Pattern", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewPatternAction_IsWiredRatherThanPromised()
    {
        var body = await _fixture.Client.GetStringAsync("/js/app.js");

        var start = body.IndexOf("window.createNewPattern", StringComparison.Ordinal);
        Assert.True(start >= 0, "js/app.js no longer defines createNewPattern");
        var action = body[start..body.IndexOf("};", start, StringComparison.Ordinal)];

        // The dashboard's New Pattern button used to raise a "Coming soon" toast and do nothing.
        Assert.DoesNotContain("Coming soon", action, StringComparison.Ordinal);
        Assert.Contains("pattern-editor.html", action, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/patternselection/conflicts")]
    [InlineData("/api/patternselection/stats")]
    public async Task PatternSelectionReadEndpoint_AnswersJson(string path)
    {
        var response = await _fixture.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET {path} returned {(int)response.StatusCode}: {body}");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("{", body.TrimStart());
    }

    /// <summary>
    /// The Settings tab Advanced Features list must be JS-driven rather than hard-coded, so
    /// a user who has disabled Voice Integration actually sees it as disabled instead of green.
    /// Verified by three invariants:
    /// (1) The container has an id the JS can target — without the id, document.getElementById
    ///     returns null and clear()/append() silently do nothing.
    /// (2) loadSettings calls /api/usersettings/current to fetch the runtime feature state.
    /// (3) The API endpoint answers JSON when the server is up.
    /// </summary>
    [Fact]
    public async Task AdvancedFeatureList_IsJSDrivenRatherThanHardcoded()
    {
        var html = await _fixture.Client.GetStringAsync("/index.html");
        var js   = await _fixture.Client.GetStringAsync("/js/app.js");

        // (1) The container must carry an id so JS can find it.
        Assert.Contains("id=\"advancedFeatureList\"", html, StringComparison.Ordinal);

        // (2) loadSettings must call the feature-state endpoint.
        var loadStart = js.IndexOf("async loadSettings(", StringComparison.Ordinal);
        Assert.True(loadStart >= 0, "js/app.js no longer contains loadSettings");
        var loadEnd = js.IndexOf("\n    }", loadStart + 1, StringComparison.Ordinal);
        var loadBody = js[loadStart..(loadEnd > 0 ? loadEnd : js.Length)];
        Assert.Contains("/api/usersettings/current", loadBody, StringComparison.Ordinal);

        // (3) The API endpoint itself must answer JSON when the server is up.
        var resp = await _fixture.Client.GetAsync("/api/usersettings/current");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(
            resp.IsSuccessStatusCode,
            $"GET /api/usersettings/current returned {(int)resp.StatusCode}: {body}");
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("{", body.TrimStart());
    }
}
