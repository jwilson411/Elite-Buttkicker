using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EDButtkicker.Hosting;

/// <summary>One mapped operation: the HTTP method, the route template, and what maps it.</summary>
public sealed record EndpointDescription(string Method, string Template, string DisplayName);

/// <summary>
/// The list of endpoints the application actually maps, read from the routing table rather than
/// from a hand-kept document, and rendered as a small OpenAPI 3.0 description.
/// It exists so "which URLs does this build serve?" has an answer that cannot drift: it is
/// generated from the same <see cref="EndpointDataSource"/> the router matches against.
/// Served on loopback only, like everything else here, and it describes shapes rather than data.
/// </summary>
public static class EndpointInventory
{
    /// <summary>Where the document is served. Under /api so the SPA fallback never shadows it.</summary>
    public const string Path = "/api/openapi";

    /// <summary>Strips route constraints, defaults and catch-all markers: <c>{*name:regex}</c> to <c>{name}</c>.</summary>
    private static readonly Regex ParameterSyntax = new(@"\{\*{0,2}([^:=?}]+)[^}]*\}", RegexOptions.Compiled);

    /// <summary>Every routable (method, template) pair currently mapped, sorted for stable output.</summary>
    public static IReadOnlyList<EndpointDescription> Describe(EndpointDataSource endpoints)
    {
        var described = new List<EndpointDescription>();

        foreach (var endpoint in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;

            // No declared method means the SPA fallback, which answers a page rather than an
            // operation and has nothing to describe.
            if (methods == null || methods.Count == 0)
            {
                continue;
            }

            var template = NormalizeTemplate(endpoint.RoutePattern.RawText);

            described.AddRange(methods.Select(method =>
                new EndpointDescription(method.ToUpperInvariant(), template, endpoint.DisplayName ?? template)));
        }

        return described
            .OrderBy(d => d.Template, StringComparer.Ordinal)
            .ThenBy(d => d.Method, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The same inventory as an OpenAPI document a local tool can read.</summary>
    public static object BuildOpenApiDocument(EndpointDataSource endpoints)
    {
        var paths = new SortedDictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);

        foreach (var endpoint in Describe(endpoints))
        {
            if (!paths.TryGetValue(endpoint.Template, out var operations))
            {
                operations = new Dictionary<string, object>(StringComparer.Ordinal);
                paths[endpoint.Template] = operations;
            }

            // A method mapped twice on one template would silently overwrite here, so keep the
            // first and let the duplicate show up as the routing conflict it is.
            operations.TryAdd(endpoint.Method.ToLowerInvariant(), new
            {
                summary = endpoint.DisplayName,
                operationId = OperationId(endpoint),
                parameters = ParameterNames(endpoint.Template)
                    .Select(name => new
                    {
                        name,
                        // Every template parameter here is a required path segment.
                        @in = "path",
                        required = true,
                        schema = new { type = "string" }
                    })
                    .ToArray(),
                responses = new Dictionary<string, object>
                {
                    ["200"] = new { description = "The request was handled." },
                    ["4XX"] = new { description = "The request was refused; the body carries an error sentence." }
                }
            });
        }

        return new
        {
            openapi = "3.0.1",
            info = new
            {
                title = "Elite Dangerous Buttkicker local API",
                version = "1.0.0",
                description =
                    "Generated from the running endpoint table. Loopback only - this server refuses " +
                    "state-changing requests that cannot prove they came from its own UI."
            },
            servers = new[] { new { url = $"http://localhost:{WebUiConfiguration.Port}" } },
            paths
        };
    }

    /// <summary>A route template as OpenAPI spells it: leading slash, bare <c>{name}</c> parameters.</summary>
    private static string NormalizeTemplate(string? rawText)
    {
        var template = ParameterSyntax.Replace(rawText ?? string.Empty, "{$1}");

        return template.StartsWith('/') ? template : "/" + template;
    }

    private static IEnumerable<string> ParameterNames(string template) =>
        ParameterSyntax.Matches(template).Select(match => match.Groups[1].Value);

    /// <summary>A stable id per operation, derived from the route rather than from the handler name.</summary>
    private static string OperationId(EndpointDescription endpoint)
    {
        var route = ParameterSyntax.Replace(endpoint.Template, "$1")
            .Replace('/', '_')
            .Trim('_');

        return $"{endpoint.Method.ToLowerInvariant()}_{route}";
    }
}
