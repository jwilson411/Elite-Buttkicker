using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace EDButtkicker.Hosting;

/// <summary>
/// The gate in front of the whole web pipeline. The listener is loopback only, but "loopback only"
/// is not a permission check: any page the user visits can make their browser POST to
/// http://localhost:47811. So every state-changing request has to prove it came from this
/// application's own UI - a loopback Host, no foreign Origin, and the process anti-forgery token.
/// Safe methods (GET/HEAD/OPTIONS) are untouched, which keeps static files, the HTML page and every
/// read-only API readable.
/// </summary>
public static class LocalhostRequestGuard
{
    /// <summary>Exact Host header values this application answers to. Matching the raw header (rather
    /// than a parsed host) is what rejects <c>localhost:47811.evil.com</c>, whose port segment is not
    /// a number and therefore parses back to a bare "localhost".</summary>
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        $"localhost:{WebUiConfiguration.Port}",
        "127.0.0.1",
        $"127.0.0.1:{WebUiConfiguration.Port}",
        "[::1]",
        $"[::1]:{WebUiConfiguration.Port}"
    };

    /// <summary>The only origins that are this application.</summary>
    private static readonly HashSet<string> AllowedOrigins = new(StringComparer.OrdinalIgnoreCase)
    {
        $"http://localhost:{WebUiConfiguration.Port}",
        $"http://127.0.0.1:{WebUiConfiguration.Port}",
        $"http://[::1]:{WebUiConfiguration.Port}"
    };

    public static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    /// <summary>
    /// Returns null when the mutation may proceed, otherwise the reason it was refused.
    /// A missing Origin is not trusted on its own - it still needs the loopback Host and the token,
    /// which is exactly what a same-origin fetch from our own page carries.
    /// </summary>
    public static string? Validate(HttpContext context, CsrfTokenProvider tokens)
    {
        // The header exactly as it was sent, not HttpRequest.Host: parsing a host can normalise away
        // the very trickery this check exists to catch, and a repeated Host header must not be able
        // to smuggle one allowed value past the allowlist either.
        var sent = context.Request.Headers.Host;
        var host = sent.Count == 1 ? sent[0] : null;
        if (string.IsNullOrEmpty(host) || !AllowedHosts.Contains(host))
        {
            return "host is not the local application";
        }

        var origin = context.Request.Headers["Origin"].ToString();
        if (!string.IsNullOrEmpty(origin) && !AllowedOrigins.Contains(origin))
        {
            return "origin is not the local application";
        }

        var header = context.Request.Headers[CsrfTokenProvider.HeaderName].ToString();
        if (!tokens.Matches(header))
        {
            return $"missing or invalid {CsrfTokenProvider.HeaderName}";
        }

        // Double submit: when the browser sent the cookie it has to be the same token, so a stale
        // tab from a previous run is refused rather than half trusted.
        if (context.Request.Cookies.TryGetValue(CsrfTokenProvider.CookieName, out var cookie) &&
            !tokens.Matches(cookie))
        {
            return "anti-forgery cookie does not match";
        }

        return null;
    }

    /// <summary>The refusal the caller sees: 403 and a reason, never the handler's side effects.</summary>
    public static async Task WriteRejectionAsync(HttpContext context, string reason)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = "Request rejected: this endpoint only accepts same-origin requests from the local UI.",
            reason
        }));
    }
}
