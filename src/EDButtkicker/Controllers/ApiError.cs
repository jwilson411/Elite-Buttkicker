using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace EDButtkicker.Controllers;

/// <summary>
/// The one shape an API failure takes: a stable sentence the web page can show and a caller can
/// match on. Exception text, stack traces and local filesystem paths never go in here - they go to
/// the log the operator already has, because everything written to a response is read by a browser.
/// </summary>
public static class ApiError
{
    /// <summary>The body for a controller result, e.g. <c>StatusCode(500, ApiError.Payload("..."))</c>.</summary>
    public static object Payload(string error) => new { error };

    /// <summary>
    /// Writes the same body straight to a raw <see cref="HttpContext"/> handler. A response that has
    /// already started is left alone rather than throwing over the original failure.
    /// </summary>
    public static Task WriteAsync(HttpContext context, int statusCode, string error)
    {
        if (context.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        return context.Response.WriteAsync(JsonSerializer.Serialize(Payload(error)));
    }
}
