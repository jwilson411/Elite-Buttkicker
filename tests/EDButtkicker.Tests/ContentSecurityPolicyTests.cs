using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The UI's defence against a hostile string that reaches the page - a ship name, a journal event,
/// a folder path - is that markup is never built by concatenation and no inline script may run.
/// The second half of that is the Content-Security-Policy header, so pin it on a real response:
/// scripts come from this origin's own files, and neither inline nor eval'd code is allowed.
/// </summary>
public class ContentSecurityPolicyTests : IClassFixture<WebUiTestServerFixture>
{
    private readonly WebUiTestServerFixture _fixture;

    public ContentSecurityPolicyTests(WebUiTestServerFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("/")]
    [InlineData("/api/csrf")]
    public async Task Response_CarriesAScriptSrcSelfPolicy(string path)
    {
        // The same loopback Host, Origin and token the application's own page sends.
        var response = await _fixture.Client.GetAsync(path);

        Assert.True(
            response.Headers.TryGetValues("Content-Security-Policy", out var values),
            $"GET {path} returned no Content-Security-Policy header");

        var policy = string.Join(' ', values!);

        Assert.Contains("script-src 'self'", policy);
        Assert.DoesNotContain("unsafe-eval", policy);

        // Only script-src has to be free of 'unsafe-inline'; style-src keeps it, because the pages
        // hide panels with a style attribute and a stylesheet cannot execute.
        Assert.DoesNotContain("unsafe-inline", ScriptSrc(policy));
    }

    private static string ScriptSrc(string policy)
    {
        var directive = policy
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(part => part.StartsWith("script-src", StringComparison.Ordinal));

        Assert.NotNull(directive);
        return directive!;
    }
}
