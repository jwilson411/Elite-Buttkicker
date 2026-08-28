using Microsoft.AspNetCore.Http;
using System.Text.Json;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging;

namespace EDButtkicker.Controllers;

/// <summary>
/// Serves the dashboard's health list and the per-subsystem retry behind each indicator.
/// </summary>
public class HealthApiController
{
    private readonly ILogger<HealthApiController> _logger;
    private readonly SystemHealthService _health;

    public HealthApiController(ILogger<HealthApiController> logger, SystemHealthService health)
    {
        _logger = logger;
        _health = health;
    }

    public async Task GetHealth(HttpContext context)
    {
        try
        {
            await WriteJsonAsync(context, Serialize(_health.GetReport()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building the health report");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    /// <summary>Runs the retry for one subsystem and answers with the state it produced.</summary>
    public async Task RetryComponent(HttpContext context, string componentId)
    {
        try
        {
            if (!SystemHealthService.CanRetry(componentId))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    error = "No retry is available for this component",
                    component = componentId
                }));
                return;
            }

            var indicator = await _health.RetryAsync(componentId, context.RequestAborted);

            if (indicator == null)
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    error = "Unknown health component",
                    component = componentId
                }));
                return;
            }

            await WriteJsonAsync(context, new
            {
                retried = componentId,
                component = SerializeIndicator(indicator),
                health = Serialize(_health.GetReport())
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrying health component {Component}", componentId);
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    /// <summary>Shared shape so the wizard and the dashboard read identical health data.</summary>
    internal static object Serialize(SystemHealthReport report) => new
    {
        status = report.Status,
        generated_at = report.GeneratedAtUtc,
        components = report.Components.Select(SerializeIndicator).ToList()
    };

    internal static object SerializeIndicator(HealthIndicator indicator) => new
    {
        id = indicator.Id,
        name = indicator.Name,
        status = indicator.Status,
        reason = indicator.Reason,
        detail = indicator.Detail,
        retry = indicator.Retry == null
            ? null
            : new
            {
                endpoint = indicator.Retry.Endpoint,
                method = indicator.Retry.Method,
                label = indicator.Retry.Label
            }
    };

    private static Task WriteJsonAsync(HttpContext context, object payload)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }
}
