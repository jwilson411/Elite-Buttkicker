using System.Text.Json;
using EDButtkicker.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// A TestServer client that looks like the application's own page: it addresses the loopback host
/// on the real port, sends the same-origin Origin header, and carries the anti-forgery token it
/// picked up from the token endpoint. Every existing integration test posts through one of these,
/// so the guard is exercised rather than bypassed.
/// </summary>
internal static class LoopbackTestClient
{
    public static readonly string Origin = $"http://localhost:{WebUiConfiguration.Port}";

    public static HttpClient Create(IWebHost host)
    {
        var client = host.GetTestClient();
        client.BaseAddress = new Uri(Origin + "/");
        client.DefaultRequestHeaders.Add("Origin", Origin);
        client.DefaultRequestHeaders.Add(CsrfTokenProvider.HeaderName, FetchToken(client));

        return client;
    }

    /// <summary>
    /// The same GET the browser page makes on load. Run on the thread pool so a synchronous fixture
    /// constructor cannot deadlock against a test framework synchronization context.
    /// </summary>
    private static string FetchToken(HttpClient client) =>
        Task.Run(async () =>
        {
            var response = await client.GetAsync("/api/csrf");
            Assert.True(response.IsSuccessStatusCode, $"GET /api/csrf returned {(int)response.StatusCode}");

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.GetProperty("token").GetString()!;
        }).GetAwaiter().GetResult();
}
