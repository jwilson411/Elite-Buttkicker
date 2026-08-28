using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using EDButtkicker.Controllers;

namespace EDButtkicker.Hosting;

/// <summary>
/// The web pipeline - static files plus the middleware router - as a reusable Configure callback.
/// Program attaches it to the generic host so requests resolve controllers out of the primary
/// service provider; the integration tests attach the exact same callback to a TestServer.
/// </summary>
public static class WebUiConfiguration
{
    /// <summary>Elite Dangerous Buttkicker - uncommon port, loopback only.</summary>
    public const int Port = 47811;

    /// <summary>
    /// Wires static file serving and the API routes onto <paramref name="app"/>.
    /// Everything is resolved from <c>context.RequestServices</c>, i.e. the host's provider.
    /// </summary>
    public static void Configure(IApplicationBuilder app)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(WebUiConfiguration).FullName!);

        var webRootPath = ResolveWebRootPath(logger);

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(webRootPath),
            RequestPath = ""
        });

        // Simple middleware-based routing
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.ToString();
            var method = context.Request.Method;

            try
            {
                // Configuration API
                if (path == "/api/config" && method == "GET")
                {
                    var controller = context.RequestServices.GetService<ConfigurationApiController>();
                    await controller!.GetConfiguration(context);
                    return;
                }
                else if (path == "/api/config" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<ConfigurationApiController>();
                    await controller!.UpdateConfiguration(context);
                    return;
                }
                else if (path == "/api/config/export" && method == "GET")
                {
                    var controller = context.RequestServices.GetService<ConfigurationApiController>();
                    await controller!.ExportConfiguration(context);
                    return;
                }
                else if (path == "/api/config/import" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<ConfigurationApiController>();
                    await controller!.ImportConfiguration(context);
                    return;
                }
                // Pattern API
                else if (path == "/api/patterns" && method == "GET")
                {
                    var controller = context.RequestServices.GetService<PatternApiController>();
                    await controller!.GetPatterns(context);
                    return;
                }
                else if (path == "/api/patterns" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<PatternApiController>();
                    await controller!.CreatePattern(context);
                    return;
                }
                else if (path.StartsWith("/api/patterns/") && path.EndsWith("/test") && method == "POST")
                {
                    var controller = context.RequestServices.GetService<PatternApiController>();
                    await controller!.TestPattern(context);
                    return;
                }
                else if (path.StartsWith("/api/patterns/") && method == "PUT")
                {
                    var controller = context.RequestServices.GetService<PatternApiController>();
                    await controller!.UpdatePattern(context);
                    return;
                }
                else if (path.StartsWith("/api/patterns/") && method == "DELETE")
                {
                    var controller = context.RequestServices.GetService<PatternApiController>();
                    await controller!.DeletePattern(context);
                    return;
                }
                else if (path == "/api/patterns/test/custom" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<PatternApiController>();
                    await controller!.TestCustomPattern(context);
                    return;
                }
                // Audio API
                else if (path == "/api/audio/devices" && method == "GET")
                {
                    var controller = context.RequestServices.GetService<AudioApiController>();
                    await controller!.GetAudioDevices(context);
                    return;
                }
                else if (path == "/api/audio/device" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<AudioApiController>();
                    await controller!.SetAudioDevice(context);
                    return;
                }
                else if (path == "/api/audio/test" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<AudioApiController>();
                    await controller!.TestAudio(context);
                    return;
                }
                // Journal API
                else if (path == "/api/journal/status" && method == "GET")
                {
                    var controller = context.RequestServices.GetService<JournalApiController>();
                    await controller!.GetJournalStatus(context);
                    return;
                }
                else if (path == "/api/journal/path" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<JournalApiController>();
                    await controller!.SetJournalPath(context);
                    return;
                }
                else if (path == "/api/journal/events/recent" && method == "GET")
                {
                    var controller = context.RequestServices.GetService<JournalApiController>();
                    await controller!.GetRecentEvents(context);
                    return;
                }
                else if (path == "/api/journal/replay/start" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<JournalApiController>();
                    await controller!.StartJournalReplay(context);
                    return;
                }
                else if (path == "/api/journal/replay/stop" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<JournalApiController>();
                    await controller!.StopJournalReplay(context);
                    return;
                }
                else if (path == "/api/journal/replay/status" && method == "GET")
                {
                    var controller = context.RequestServices.GetService<JournalApiController>();
                    await controller!.GetJournalReplayStatus(context);
                    return;
                }
                // Pattern Files API
                else if (path == "/api/PatternFiles/reload" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<PatternFilesController>();
                    await controller!.ReloadPatternFilesHttpContext(context);
                    return;
                }
                else if (path == "/api/PatternFiles/export" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<PatternFilesController>();
                    await controller!.ExportPatternPack(context);
                    return;
                }
                else if (path == "/api/PatternFiles/import" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<PatternFilesController>();
                    await controller!.ImportPatternFile(context);
                    return;
                }
                else if (path == "/api/PatternFiles/packs" && method == "GET")
                {
                    var controller = context.RequestServices.GetService<PatternFilesController>();
                    await controller!.GetPatternPacks(context);
                    return;
                }
                // Pattern Editor API
                else if (path == "/api/PatternEditor/templates" && method == "GET")
                {
                    var controller = context.RequestServices.GetService<PatternEditorController>();
                    await controller!.GetPatternTemplatesHttpContext(context);
                    return;
                }
                else if (path == "/api/PatternEditor/create" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<PatternEditorController>();
                    await controller!.CreateNewPatternHttpContext(context);
                    return;
                }
                else if (path == "/api/PatternEditor/save" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<PatternEditorController>();
                    await controller!.SavePatternHttpContext(context);
                    return;
                }
                else if (path == "/api/PatternEditor/validate" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<PatternEditorController>();
                    await controller!.ValidatePatternHttpContext(context);
                    return;
                }
                else if (path == "/api/PatternEditor/test" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<PatternEditorController>();
                    await controller!.TestPatternHttpContext(context);
                    return;
                }
                else if (path.StartsWith("/api/PatternEditor/load/") && method == "GET")
                {
                    var fileName = path.Substring("/api/PatternEditor/load/".Length);
                    var controller = context.RequestServices.GetService<PatternEditorController>();
                    await controller!.LoadPatternForEditingHttpContext(context, fileName);
                    return;
                }
                else if (path.StartsWith("/api/PatternEditor/user-files/") && method == "GET")
                {
                    var author = path.Substring("/api/PatternEditor/user-files/".Length);
                    var controller = context.RequestServices.GetService<PatternEditorController>();
                    await controller!.GetUserFilesHttpContext(context, author);
                    return;
                }
                // First-run setup API
                else if (path == "/api/setup/status" && method == "GET")
                {
                    var controller = context.RequestServices.GetService<SetupApiController>();
                    await controller!.GetStatus(context);
                    return;
                }
                else if (path == "/api/setup/journal/candidates" && method == "GET")
                {
                    var controller = context.RequestServices.GetService<SetupApiController>();
                    await controller!.GetJournalCandidates(context);
                    return;
                }
                else if (path == "/api/setup/journal" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<SetupApiController>();
                    await controller!.ConfirmJournalPath(context);
                    return;
                }
                else if (path == "/api/setup/audio/device" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<SetupApiController>();
                    await controller!.SelectAudioDevice(context);
                    return;
                }
                else if (path == "/api/setup/audio/test" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<SetupApiController>();
                    await controller!.RunAudioTest(context);
                    return;
                }
                else if (path == "/api/setup/complete" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<SetupApiController>();
                    await controller!.CompleteSetup(context);
                    return;
                }
                else if (path == "/api/setup/reopen" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<SetupApiController>();
                    await controller!.ReopenSetup(context);
                    return;
                }
                // Health API
                else if (path == "/api/health" && method == "GET")
                {
                    var controller = context.RequestServices.GetService<HealthApiController>();
                    await controller!.GetHealth(context);
                    return;
                }
                else if (path.StartsWith("/api/health/") && path.EndsWith("/retry") && method == "POST")
                {
                    var componentId = path["/api/health/".Length..^"/retry".Length];
                    var controller = context.RequestServices.GetService<HealthApiController>();
                    await controller!.RetryComponent(context, componentId);
                    return;
                }
                // Contextual Intelligence API
                else if (path == "/api/context/status" && method == "GET")
                {
                    var controller = context.RequestServices.GetService<ContextualIntelligenceApiController>();
                    await controller!.GetContextualIntelligenceStatus(context);
                    return;
                }
                else if (path == "/api/context/config" && method == "POST")
                {
                    var controller = context.RequestServices.GetService<ContextualIntelligenceApiController>();
                    await controller!.UpdateContextualIntelligenceConfig(context);
                    return;
                }
                else if (path == "/api/context/predictions" && method == "GET")
                {
                    var controller = context.RequestServices.GetService<ContextualIntelligenceApiController>();
                    await controller!.GetGameContextPredictions(context);
                    return;
                }
                // Default route - serve the main UI
                else if (path == "/" || path == "/index.html" || !path.StartsWith("/api/"))
                {
                    context.Response.ContentType = "text/html";
                    var html = await GetMainHtmlPage(webRootPath);
                    var bytes = System.Text.Encoding.UTF8.GetBytes(html);
                    await context.Response.Body.WriteAsync(bytes, 0, bytes.Length);
                    return;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error handling request: {Path}", path);
                context.Response.StatusCode = 500;
                var errorBytes = System.Text.Encoding.UTF8.GetBytes("Internal server error");
                await context.Response.Body.WriteAsync(errorBytes, 0, errorBytes.Length);
                return;
            }

            await next();
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

                <div class="loading">
                    🚀 Loading configuration interface...
                    <br><br>
                    <small>Please wait while the advanced pattern system initializes</small>
                </div>
            </div>

            <script>
                console.log('Elite Dangerous Buttkicker Configuration Interface');
                console.log('Web server running on localhost:8080');

                // Basic status check
                setTimeout(() => {
                    document.querySelector('.loading').innerHTML = `
                        <h3>📡 Configuration Interface Ready</h3>
                        <p>API endpoints are available for pattern configuration</p>
                        <br>
                        <p><strong>Available endpoints:</strong></p>
                        <ul style="text-align: left; max-width: 600px; margin: 0 auto;">
                            <li>GET /api/config - Current configuration</li>
                            <li>GET /api/patterns - All haptic patterns</li>
                            <li>GET /api/audio/devices - Available audio devices</li>
                            <li>GET /api/journal/status - Journal monitoring status</li>
                            <li>POST /api/patterns/{eventType}/test - Test patterns</li>
                        </ul>
                        <br>
                        <p><em>Advanced web UI will load here automatically...</em></p>
                    `;
                }, 2000);
            </script>
        </body>
        </html>
        """;
    }
}
