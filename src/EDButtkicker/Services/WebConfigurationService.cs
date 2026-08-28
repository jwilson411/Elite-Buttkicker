using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using EDButtkicker.Hosting;
using System.Diagnostics;

namespace EDButtkicker.Services;

/// <summary>
/// The web app is hosted by the generic host (see Program.CreateHostBuilder), so it resolves
/// controllers from the primary service provider. This service no longer builds a second
/// WebHost - it only announces the interface and opens the browser once the server is up.
/// </summary>
public class WebConfigurationService : BackgroundService
{
    private readonly ILogger<WebConfigurationService> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly int _port = WebUiConfiguration.Port;

    public WebConfigurationService(
        ILogger<WebConfigurationService> logger,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Web Configuration Server listening on localhost:{Port}", _port);

        try
        {
            await WaitForApplicationStartedAsync(stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            _logger.LogInformation("✅ Web Configuration Interface started!");
            _logger.LogInformation("🌐 Opening browser at: http://localhost:{Port}", _port);
            _logger.LogInformation("📱 Configure patterns, audio devices, and monitor Elite Dangerous events");

            // Automatically open the web browser
            OpenBrowser($"http://localhost:{_port}");
        }
        catch (OperationCanceledException)
        {
            // Shutting down before the host finished starting - nothing to announce.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to announce the web configuration server");
        }
    }

    private async Task WaitForApplicationStartedAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var startedRegistration = _lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var stoppingRegistration = stoppingToken.Register(() => started.TrySetResult());

        await started.Task;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Web Configuration Server");
        await base.StopAsync(cancellationToken);
    }

    private void OpenBrowser(string url)
    {
        try
        {
            // Cross-platform browser opening
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", url);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }

            _logger.LogInformation("Browser launched successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to automatically open browser. Please manually navigate to: {Url}", url);
        }
    }
}
