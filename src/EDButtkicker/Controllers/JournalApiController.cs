using Microsoft.AspNetCore.Http;
using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Models;
using Microsoft.Extensions.Logging;

namespace EDButtkicker.Controllers;

public class JournalApiController
{
    private readonly ILogger<JournalApiController> _logger;
    private readonly AppSettings _settings;
    private static readonly List<JournalEvent> RecentEvents = new();
    private static readonly object EventsLock = new object();

    public JournalApiController(ILogger<JournalApiController> logger, AppSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public async Task GetJournalStatus(HttpContext context)
    {
        try
        {
            var journalPath = _settings.EliteDangerous.JournalPath;
            var pathExists = !string.IsNullOrEmpty(journalPath) && Directory.Exists(journalPath);
            
            List<string> journalFiles = new();
            if (pathExists)
            {
                try
                {
                    journalFiles = Directory.GetFiles(journalPath, "Journal.*.log")
                        .OrderByDescending(f => File.GetLastWriteTime(f))
                        .Take(5)
                        .Select(Path.GetFileName)
                        .Where(name => name != null)
                        .Cast<string>()
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error reading journal files");
                }
            }

            var status = new
            {
                journal_path = journalPath,
                path_exists = pathExists,
                monitoring = pathExists,
                monitor_latest_only = _settings.EliteDangerous.MonitorLatestOnly,
                recent_files = journalFiles,
                events_processed = GetRecentEventsCount(),
                last_event_time = GetLastEventTime(),
                status = pathExists ? "Connected" : "Disconnected",
                health = pathExists ? "Healthy" : "Configuration Required"
            };

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(status, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting journal status");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    public async Task SetJournalPath(HttpContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var json = await reader.ReadToEndAsync();
            
            if (string.IsNullOrEmpty(json))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Request body is empty" }));
                return;
            }

            var pathData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (pathData == null || !pathData.ContainsKey("path"))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Path is required" }));
                return;
            }

            var journalPath = pathData["path"].ToString()?.Trim();
            if (string.IsNullOrEmpty(journalPath))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Path cannot be empty" }));
                return;
            }

            // Expand environment variables
            if (journalPath.Contains("%USERPROFILE%"))
            {
                journalPath = journalPath.Replace("%USERPROFILE%", 
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }

            if (!Directory.Exists(journalPath))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new 
                { 
                    error = "Directory does not exist",
                    path = journalPath
                }));
                return;
            }

            // Check for journal files
            var journalFiles = Directory.GetFiles(journalPath, "Journal.*.log");
            if (journalFiles.Length == 0)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new 
                { 
                    error = "No Elite Dangerous journal files found in this directory",
                    path = journalPath
                }));
                return;
            }

            _settings.EliteDangerous.JournalPath = journalPath;
            
            _logger.LogInformation("Journal path updated to: {JournalPath}", journalPath);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new 
            { 
                success = true, 
                message = "Journal path updated successfully",
                path = journalPath,
                journal_files_found = journalFiles.Length
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting journal path");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    public async Task GetRecentEvents(HttpContext context)
    {
        try
        {
            var limit = 50; // Default limit
            if (context.Request.Query.ContainsKey("limit"))
            {
                if (int.TryParse(context.Request.Query["limit"], out int requestedLimit))
                {
                    limit = Math.Min(Math.Max(requestedLimit, 1), 500); // Between 1 and 500
                }
            }

            List<object> events;
            lock (EventsLock)
            {
                events = RecentEvents
                    .OrderByDescending(e => e.Timestamp)
                    .Take(limit)
                    .Select(e => new
                    {
                        timestamp = e.Timestamp,
                        @event = e.Event,
                        star_system = e.StarSystem,
                        station_name = e.StationName,
                        health = e.Health,
                        hull_damage = e.HullDamage,
                        additional_data = e.AdditionalData
                    })
                    .Cast<object>()
                    .ToList();
            }

            var response = new
            {
                events = events,
                metadata = new
                {
                    total_events = events.Count,
                    limit_applied = limit,
                    monitoring = !string.IsNullOrEmpty(_settings.EliteDangerous.JournalPath) && 
                                Directory.Exists(_settings.EliteDangerous.JournalPath)
                }
            };

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(response, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recent events");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    public static void AddRecentEvent(JournalEvent journalEvent)
    {
        lock (EventsLock)
        {
            RecentEvents.Insert(0, journalEvent);
            
            // Keep only recent events (last 1000)
            if (RecentEvents.Count > 1000)
            {
                RecentEvents.RemoveRange(1000, RecentEvents.Count - 1000);
            }
        }
    }

    private int GetRecentEventsCount()
    {
        lock (EventsLock)
        {
            return RecentEvents.Count;
        }
    }

    private DateTime? GetLastEventTime()
    {
        lock (EventsLock)
        {
            return RecentEvents.FirstOrDefault()?.Timestamp;
        }
    }
}