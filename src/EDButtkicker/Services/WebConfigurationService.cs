using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Http;
using EDButtkicker.Configuration;
using EDButtkicker.Controllers;

namespace EDButtkicker.Services;

public class WebConfigurationService : BackgroundService
{
    private readonly ILogger<WebConfigurationService> _logger;
    private readonly AppSettings _settings;
    private readonly AudioEngineService _audioEngine;
    private readonly EventMappingService _eventMapping;
    private readonly VoiceFeedbackService _voiceService;
    private readonly PatternSequencer _patternSequencer;
    private readonly ContextualIntelligenceService _contextualIntelligence;
    private IWebHost? _webHost;
    private readonly int _port = 8080;

    public WebConfigurationService(
        ILogger<WebConfigurationService> logger,
        AppSettings settings,
        AudioEngineService audioEngine,
        EventMappingService eventMapping,
        VoiceFeedbackService voiceService,
        PatternSequencer patternSequencer,
        ContextualIntelligenceService contextualIntelligence)
    {
        _logger = logger;
        _settings = settings;
        _audioEngine = audioEngine;
        _eventMapping = eventMapping;
        _voiceService = voiceService;
        _patternSequencer = patternSequencer;
        _contextualIntelligence = contextualIntelligence;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Web Configuration Server on localhost:{Port}", _port);

        try
        {
            _webHost = new WebHostBuilder()
                .UseKestrel(options =>
                {
                    options.ListenLocalhost(_port);
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton(_settings);
                    services.AddSingleton(_audioEngine);
                    services.AddSingleton(_eventMapping);
                    services.AddSingleton(_voiceService);
                    services.AddSingleton(_patternSequencer);
                    services.AddSingleton<ConfigurationApiController>();
                    services.AddSingleton<PatternApiController>();
                    services.AddSingleton<AudioApiController>();
                    services.AddSingleton<JournalApiController>();
                    services.AddSingleton<ContextualIntelligenceApiController>();
                })
                .Configure(app =>
                {
                    app.UseStaticFiles();
                    
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
                                var html = await GetMainHtmlPage();
                                using var writer = new StreamWriter(context.Response.Body);
                                await writer.WriteAsync(html);
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error handling request: {Path}", path);
                            context.Response.StatusCode = 500;
                            using var writer = new StreamWriter(context.Response.Body);
                            await writer.WriteAsync("Internal server error");
                            return;
                        }
                        
                        await next();
                    });
                })
                .UseContentRoot(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"))
                .Build();

            await _webHost.StartAsync(stoppingToken);

            _logger.LogInformation("✅ Web Configuration Interface started!");
            _logger.LogInformation("🌐 Open your browser and navigate to: http://localhost:{Port}", _port);
            _logger.LogInformation("📱 Configure patterns, audio devices, and monitor Elite Dangerous events");
            
            await _webHost.WaitForShutdownAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start web configuration server");
        }
    }

    private async Task<string> GetMainHtmlPage()
    {
        var htmlPath = Path.Combine("wwwroot", "index.html");
        
        if (File.Exists(htmlPath))
        {
            return await File.ReadAllTextAsync(htmlPath);
        }
        
        // Return embedded HTML if file doesn't exist
        return GetEmbeddedHtml();
    }
    
    private string GetEmbeddedHtml()
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Web Configuration Server");
        
        if (_webHost != null)
        {
            await _webHost.StopAsync(cancellationToken);
            _webHost.Dispose();
        }
        
        await base.StopAsync(cancellationToken);
    }
}