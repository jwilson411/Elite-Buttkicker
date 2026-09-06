using System.Text.Json;
using EDButtkicker.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The routing table is now the only description of what this build serves, so these tests hold it
/// to that: the generated document is served and well formed, and every route the UI and the DI
/// graph tests exercise is actually present in the table rather than reached by a hand-written
/// dispatch chain that could go missing without anything noticing.
/// </summary>
public class EndpointInventoryTests : IClassFixture<WebUiTestServerFixture>
{
    private readonly WebUiTestServerFixture _fixture;

    public EndpointInventoryTests(WebUiTestServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task OpenApiDocument_IsServedAsJson()
    {
        var response = await _fixture.Client.GetAsync(EndpointInventory.Path);

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var version = document.RootElement.GetProperty("openapi").GetString();

        Assert.NotNull(version);
        Assert.StartsWith("3.", version);
    }

    /// <summary>
    /// Every route the DI graph test sends a request to, minus the SPA root, has to be described by
    /// the inventory - a route that answers but is invisible to the routing table would mean the
    /// dispatch happened somewhere else.
    /// </summary>
    [Theory]
    [MemberData(nameof(DescribableRoutes))]
    public void EveryMappedRoute_IsDescribedByTheInventory(string method, string path)
    {
        var described = EndpointInventory.Describe(
            _fixture.Services.GetRequiredService<EndpointDataSource>());

        Assert.True(
            described.Any(d =>
                string.Equals(d.Method, method, StringComparison.OrdinalIgnoreCase)
                && TemplateMatches(d.Template, path)),
            $"{method} {path} is not in the endpoint inventory. Described: "
                + string.Join(", ", described.Select(d => $"{d.Method} {d.Template}")));
    }

    /// <summary>
    /// <see cref="DependencyInjectionGraphTests.MappedRoutes"/> without "/", which is the SPA
    /// fallback: it answers a page rather than an operation, so it is deliberately not described.
    /// The two status GETs are named here as well - the UI polls them, and neither is covered above.
    /// </summary>
    public static TheoryData<string, string> DescribableRoutes()
    {
        var routes = new TheoryData<string, string>();

        foreach (var row in DependencyInjectionGraphTests.MappedRoutes())
        {
            var method = (string)row[0];
            var path = (string)row[1];

            if (path != "/")
            {
                routes.Add(method, path);
            }
        }

        routes.Add("GET", "/api/audio/status");
        routes.Add("GET", "/api/journal/replay/status");

        return routes;
    }

    /// <summary>
    /// A template matches a concrete path when they have the same number of segments, every literal
    /// segment is equal (case-insensitively, as routing itself compares them) and every
    /// <c>{param}</c> consumes exactly one segment.
    /// </summary>
    private static bool TemplateMatches(string template, string path)
    {
        var templateSegments = Segments(template);
        var pathSegments = Segments(path);

        if (templateSegments.Length != pathSegments.Length)
        {
            return false;
        }

        return templateSegments.Zip(pathSegments).All(pair =>
            (pair.First.StartsWith('{') && pair.First.EndsWith('}'))
            || string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] Segments(string value) =>
        value.Split('/', StringSplitOptions.RemoveEmptyEntries);
}
