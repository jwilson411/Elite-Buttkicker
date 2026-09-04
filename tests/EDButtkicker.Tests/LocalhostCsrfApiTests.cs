using System.Net;
using System.Text;
using System.Text.Json;
using EDButtkicker.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The listener is loopback only, but a page on any site can still make the user's browser post to
/// it. These run the real pipeline on a TestServer and pin that a state-changing request only gets
/// through when it proves it came from this application's own UI - and that reading stays open.
/// </summary>
public class LocalhostCsrfApiTests : IClassFixture<WebUiTestServerFixture>
{
    /// <summary>A mutation that needs nothing from the request body, so only the guard decides.</summary>
    private const string MutationPath = "/api/health/journal/retry";

    private readonly WebUiTestServerFixture _fixture;
    private readonly string _token;

    public LocalhostCsrfApiTests(WebUiTestServerFixture fixture)
    {
        _fixture = fixture;
        _token = fixture.Services.GetRequiredService<CsrfTokenProvider>().Token;
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://evil.example")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("http://localhost:8080")]
    [InlineData("null")]
    public async Task Mutation_FromAForeignOrigin_IsRejected(string origin)
    {
        // Everything else about this request is right: loopback host, real token.
        var response = await SendMutationAsync(host: $"localhost:{WebUiConfiguration.Port}", origin: origin, token: _token);

        await AssertRejectedAsync(response);
    }

    [Fact]
    public async Task Mutation_WithoutTheAntiForgeryToken_IsRejected()
    {
        var response = await SendMutationAsync(
            host: $"localhost:{WebUiConfiguration.Port}", origin: LoopbackTestClient.Origin, token: null);

        await AssertRejectedAsync(response);
    }

    [Fact]
    public async Task Mutation_WithAGuessedToken_IsRejected()
    {
        var response = await SendMutationAsync(
            host: $"localhost:{WebUiConfiguration.Port}",
            origin: LoopbackTestClient.Origin,
            token: new string('a', 64));

        await AssertRejectedAsync(response);
    }

    [Fact]
    public async Task Mutation_WithAStaleAntiForgeryCookie_IsRejected()
    {
        var request = MutationRequest($"localhost:{WebUiConfiguration.Port}", LoopbackTestClient.Origin, _token);
        request.Headers.TryAddWithoutValidation("Cookie", $"{CsrfTokenProvider.CookieName}={new string('b', 64)}");

        await AssertRejectedAsync(await _fixture.RawClient.SendAsync(request));
    }

    [Theory]
    [InlineData("evil.com")]
    [InlineData("localhost.evil.com")]
    [InlineData("localhost:47811.evil.com")]
    [InlineData("127.0.0.1.evil.com")]
    [InlineData("localhost:8080")]
    public async Task Mutation_WithAForeignHost_IsRejected(string host)
    {
        // No Origin header at all - a missing Origin is never trusted on its own.
        var response = await SendMutationAsync(host: host, origin: null, token: _token);

        await AssertRejectedAsync(response);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:47811")]
    [InlineData("127.0.0.1:47811")]
    [InlineData("[::1]:47811")]
    public async Task Mutation_WithLoopbackHostAndToken_ReachesTheHandler(string host)
    {
        var response = await SendMutationAsync(host: host, origin: null, token: _token);

        await AssertNotRejectedAsync(response);
    }

    [Theory]
    [InlineData("http://localhost:47811")]
    [InlineData("http://127.0.0.1:47811")]
    [InlineData("http://[::1]:47811")]
    public async Task Mutation_FromTheSameOriginWithAToken_ReachesTheHandler(string origin)
    {
        var response = await SendMutationAsync(host: $"localhost:{WebUiConfiguration.Port}", origin: origin, token: _token);

        await AssertNotRejectedAsync(response);
    }

    [Fact]
    public async Task CsrfEndpoint_ServesATokenAndBothCookies()
    {
        var response = await _fixture.RawClient.GetAsync("/api/csrf");

        Assert.True(response.IsSuccessStatusCode, $"GET /api/csrf returned {(int)response.StatusCode}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = document.RootElement.GetProperty("token").GetString();

        Assert.Equal(64, token!.Length);
        Assert.All(token, c => Assert.True(Uri.IsHexDigit(c), $"'{c}' is not hex"));

        var cookies = response.Headers.GetValues("Set-Cookie").ToList();

        var serverCookie = Assert.Single(cookies, c => c.StartsWith($"{CsrfTokenProvider.CookieName}="));
        Assert.Contains("httponly", serverCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", serverCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", serverCookie, StringComparison.OrdinalIgnoreCase);

        // The page needs one copy it can actually read to build the header from.
        var scriptCookie = Assert.Single(cookies, c => c.StartsWith($"{CsrfTokenProvider.ScriptCookieName}="));
        Assert.DoesNotContain("httponly", scriptCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", scriptCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/api/health")]
    [InlineData("/api/config")]
    [InlineData("/api/patterns")]
    [InlineData("/api/setup/status")]
    public async Task SafeRequests_StayReadableWithoutAToken(string path)
    {
        // No token, no Origin, and the bare default host: reads are not the attack surface.
        var response = await _fixture.RawClient.GetAsync(path);

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET {path} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private Task<HttpResponseMessage> SendMutationAsync(string host, string? origin, string? token) =>
        _fixture.RawClient.SendAsync(MutationRequest(host, origin, token));

    private static HttpRequestMessage MutationRequest(string host, string? origin, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, MutationPath)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        // Unvalidated so the malformed hosts an attacker would try survive to the server.
        request.Headers.TryAddWithoutValidation("Host", host);

        if (origin != null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (token != null)
        {
            request.Headers.TryAddWithoutValidation(CsrfTokenProvider.HeaderName, token);
        }

        return request;
    }

    private static async Task AssertRejectedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("same-origin", body);
    }

    private static async Task AssertNotRejectedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        // The handler may still answer 4xx for its own reasons; what must not happen is the guard
        // turning our own UI away.
        Assert.True(
            response.StatusCode != HttpStatusCode.Forbidden,
            $"{MutationPath} was refused by the request guard: {body}");
        Assert.DoesNotContain("Request rejected", body);
    }
}
