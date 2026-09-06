using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using EDButtkicker.Controllers;

namespace EDButtkicker.Hosting;

/// <summary>
/// The web pipeline - the security headers, the request guard, static files and the routing table -
/// as a reusable Configure callback. Program attaches it to the generic host so requests resolve
/// controllers out of the primary service provider; the integration tests attach the exact same
/// callback to a TestServer.
/// Routes themselves live on the controllers, so adding an endpoint never means editing this file.
/// </summary>
public static class WebUiConfiguration
{
    /// <summary>Elite Dangerous Buttkicker - uncommon port, loopback only.</summary>
    public const int Port = 47811;

    /// <summary>
    /// Wires static file serving and the mapped API endpoints onto <paramref name="app"/>.
    /// Everything is resolved from <c>context.RequestServices</c>, i.e. the host's provider.
    /// </summary>
    public static void Configure(IApplicationBuilder app)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(WebUiConfiguration).FullName!);

        var webRootPath = ResolveWebRootPath(logger);
        var tokens = app.ApplicationServices.GetRequiredService<CsrfTokenProvider>();

        // Second line of defence behind the DOM-building helpers in wwwroot/js/dom.js: even if a
        // value from the journal or a pattern pack were ever concatenated into markup again, the
        // browser refuses to run it. Script is 'self' only - no 'unsafe-inline', no 'unsafe-eval' -
        // which is why the pages carry no inline <script> and no onclick attributes. Style still
        // allows inline: the pages hide panels with style="display: none" and the fallback page
        // carries a <style> block, and a stylesheet cannot execute. The two cdnjs allowances are
        // the Font Awesome stylesheet and the webfonts it pulls in.
        const string contentSecurityPolicy =
            "default-src 'self'; " +
            "script-src 'self'; " +
            "style-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com; " +
            "font-src 'self' https://cdnjs.cloudflare.com; " +
            "img-src 'self' data:; " +
            "connect-src 'self'; " +
            "object-src 'none'; " +
            "base-uri 'self'; " +
            "form-action 'self'; " +
            "frame-ancestors 'none'";

        app.Use(async (context, next) =>
        {
            context.Response.Headers["Content-Security-Policy"] = contentSecurityPolicy;
            await next();
        });

        // The one place a failure nobody handled becomes a response. Handlers that already answer
        // their own errors keep doing so; anything that escapes leaves as the API's error shape and
        // nothing else, because the exception text is for the log the operator has, not the browser.
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Unhandled error handling {Method} {Path}",
                    context.Request.Method,
                    context.Request.Path.ToString());

                await ApiError.WriteAsync(context, StatusCodes.Status500InternalServerError,
                    "The request could not be completed");
            }
        });

        // Ahead of everything, including static files: a mutation that cannot prove it came from our
        // own page never reaches a handler, and a safe request leaves with the token it needs.
        app.Use(async (context, next) =>
        {
            if (LocalhostRequestGuard.IsSafeMethod(context.Request.Method))
            {
                tokens.IssueCookies(context);
                await next();
                return;
            }

            var rejection = LocalhostRequestGuard.Validate(context, tokens);
            if (rejection != null)
            {
                logger.LogWarning(
                    "Rejected {Method} {Path} (host {Host}, origin {Origin}): {Reason}",
                    context.Request.Method,
                    context.Request.Path.ToString(),
                    context.Request.Host.Value,
                    context.Request.Headers["Origin"].ToString(),
                    rejection);

                await LocalhostRequestGuard.WriteRejectionAsync(context, rejection);
                return;
            }

            await next();
        });

        // The body cap as the server's own rule, not only as something every handler remembers to
        // apply: a request that declares or streams more than this is refused by the pipeline.
        // Kestrel is configured with the same number in Program; this covers any other transport.
        app.Use(async (context, next) =>
        {
            var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly: false })
            {
                bodySize.MaxRequestBodySize = RequestLimits.MaxRequestBodyBytes;
            }

            await next();
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(webRootPath),
            RequestPath = ""
        });

        // From here on the routing table decides. Every API route lives on a controller as an
        // attribute, so adding an endpoint never means editing this file - and the inventory served
        // at /api/openapi is read from the very table matched against below.
        app.UseRouting();

        EndpointDataSource? routeTable = null;

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();

            // Anti-forgery token for the same-origin UI. A cross-origin page can issue this GET
            // but cannot read its response or its cookies, so the token stays ours.
            // The cookies are already on the response - every safe request leaves with them.
            endpoints.MapGet("/api/csrf", context =>
            {
                context.Response.ContentType = "application/json";

                return context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
                {
                    token = tokens.Token,
                    header = CsrfTokenProvider.HeaderName
                }));
            });

            // What this build actually serves, generated from the routing table rather than kept
            // by hand, so it cannot drift from the endpoints above.
            endpoints.MapGet(EndpointInventory.Path, context =>
            {
                context.Response.ContentType = "application/json";

                return context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(
                    EndpointInventory.BuildOpenApiDocument(routeTable!),
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            });

            // The single-page fallback: any path that is not an API route and not a file on disk
            // gets the dashboard, so a deep link into the UI still loads it. An unmatched /api path
            // stays a 404 - answering it with HTML would tell a caller the endpoint exists.
            endpoints.MapFallback("{*path}", async context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync(await GetMainHtmlPage(webRootPath));
            });

            // Built last so it sees every data source mapped above, including the fallback.
            routeTable = new CompositeEndpointDataSource(endpoints.DataSources);
        });
    }

    public static string ResolveWebRootPath(ILogger logger)
    {
        // Try multiple possible locations for wwwroot
        var possiblePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "EDButtkicker", "wwwroot"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "EDButtkicker", "wwwroot")
        };

        foreach (var path in possiblePaths)
        {
            if (Directory.Exists(path))
            {
                logger.LogInformation("Found wwwroot at: {Path}", path);
                return Path.GetFullPath(path);
            }
        }

        // If no wwwroot found, use the base directory (will serve embedded content)
        logger.LogWarning("wwwroot directory not found, using base directory: {Path}", AppContext.BaseDirectory);
        return AppContext.BaseDirectory;
    }

    private static async Task<string> GetMainHtmlPage(string webRootPath)
    {
        var htmlPath = Path.Combine(webRootPath, "index.html");

        if (File.Exists(htmlPath))
        {
            return await File.ReadAllTextAsync(htmlPath);
        }

        // Return embedded HTML if file doesn't exist
        return GetEmbeddedHtml();
    }

    private static string GetEmbeddedHtml()
    {
        return """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>Elite Dangerous Buttkicker Configuration</title>
            <style>
                * { margin: 0; padding: 0; box-sizing: border-box; }
                body {
                    font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
                    background: linear-gradient(135deg, #0a0a0a, #1a1a2e);
                    color: #ffffff;
                    min-height: 100vh;
                }
                .header {
                    background: linear-gradient(90deg, #ff6b35, #f7931e);
                    padding: 20px;
                    text-align: center;
                    box-shadow: 0 4px 20px rgba(255, 107, 53, 0.3);
                }
                .header h1 {
                    font-size: 2.5rem;
                    font-weight: 700;
                    text-shadow: 2px 2px 4px rgba(0,0,0,0.5);
                }
                .subtitle {
                    margin-top: 10px;
                    font-size: 1.1rem;
                    opacity: 0.9;
                }
                .loading {
                    text-align: center;
                    padding: 50px;
                    font-size: 1.2rem;
                    color: #ff6b35;
                }
                .container {
                    max-width: 1200px;
                    margin: 0 auto;
                    padding: 30px 20px;
                }
                .status-bar {
                    background: rgba(255, 255, 255, 0.1);
                    border-radius: 10px;
                    padding: 20px;
                    margin-bottom: 30px;
                    backdrop-filter: blur(10px);
                    border: 1px solid rgba(255, 255, 255, 0.1);
                }
                .status-item {
                    display: inline-block;
                    margin-right: 30px;
                    font-size: 0.95rem;
                }
                .status-indicator {
                    display: inline-block;
                    width: 10px;
                    height: 10px;
                    border-radius: 50%;
                    margin-right: 8px;
                }
                .status-online { background: #4CAF50; }
                .status-offline { background: #f44336; }
                .status-warning { background: #ff9800; }
            </style>
        </head>
        <body>
            <div class="header">
                <h1>Elite Dangerous Buttkicker Extension</h1>
                <div class="subtitle">Advanced Haptic Feedback Configuration Interface</div>
            </div>

            <div class="container">
                <div class="status-bar">
                    <div class="status-item">
                        <span class="status-indicator status-online"></span>
                        Web Interface: Online
                    </div>
                    <div class="status-item">
                        <span class="status-indicator status-warning"></span>
                        Audio Engine: Initializing
                    </div>
                    <div class="status-item">
                        <span class="status-indicator status-offline"></span>
                        Journal Monitor: Disconnected
                    </div>
                </div>

                <!--
                    This fallback page is served only when wwwroot/index.html is missing, so it has
                    no data to render and no script: the Content-Security-Policy allows no inline
                    script, and there is nothing here worth loading a file for. The endpoint list is
                    static markup rather than something a handler writes into .innerHTML.
                -->
                <div class="loading">
                    <h3>📡 Configuration Interface Ready</h3>
                    <p>The web UI files were not found, but the API is serving requests.</p>
                    <br>
                    <p><strong>Available endpoints:</strong></p>
                    <ul style="text-align: left; max-width: 600px; margin: 0 auto;">
                        <li>GET /api/config - Current configuration</li>
                        <li>GET /api/patterns - All haptic patterns</li>
                        <li>GET /api/audio/devices - Available audio devices</li>
                        <li>GET /api/journal/status - Journal monitoring status</li>
                        <li>POST /api/patterns/{eventType}/test - Test patterns</li>
                    </ul>
                </div>
            </div>
        </body>
        </html>
        """;
    }
}
