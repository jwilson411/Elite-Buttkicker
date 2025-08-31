using Microsoft.AspNetCore.Http;
using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging;

namespace EDButtkicker.Controllers;

public class JournalApiController
{
    private readonly ILogger<JournalApiController> _logger;
    private readonly AppSettings _settings;
    private readonly EventMappingService _eventMappingService;
    private static readonly List<JournalEvent> RecentEvents = new();
    private static readonly object EventsLock = new object();
    
    // Replay functionality
    private static CancellationTokenSource? _replayTokenSource;
    private static Task? _replayTask;
    private static readonly object ReplayLock = new object();

    public JournalApiController(ILogger<JournalApiController> logger, AppSettings settings, EventMappingService eventMappingService)
    {
        _logger = logger;
        _settings = settings;
        _eventMappingService = eventMappingService;
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

    public async Task StartJournalReplay(HttpContext context)
    {
        try
        {
            // Parse request body to get journal file selection
            string? selectedJournalFile = null;
            if (context.Request.ContentLength > 0)
            {
                using var reader = new StreamReader(context.Request.Body);
                var json = await reader.ReadToEndAsync();
                if (!string.IsNullOrEmpty(json))
                {
                    var requestData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                    if (requestData != null && requestData.ContainsKey("journalFile"))
                    {
                        selectedJournalFile = requestData["journalFile"].ToString();
                    }
                }
            }

            // Get events from the selected journal file or fallback to recent events (outside of lock)
            List<JournalEvent> eventsToReplay;
            if (!string.IsNullOrEmpty(selectedJournalFile))
            {
                eventsToReplay = await ReadLastFiveMinutesFromJournalFile(selectedJournalFile);
            }
            else
            {
                // Fallback to recent events from memory (last 5 minutes of real time)
                var cutoffTime = DateTime.UtcNow.AddMinutes(-5);
                lock (EventsLock)
                {
                    eventsToReplay = RecentEvents
                        .Where(e => e.Timestamp >= cutoffTime)
                        .OrderBy(e => e.Timestamp)
                        .ToList();
                }
            }
            
            // Now handle replay start/stop in lock
            lock (ReplayLock)
            {
                // Stop any existing replay
                if (_replayTokenSource != null && !_replayTokenSource.Token.IsCancellationRequested)
                {
                    _replayTokenSource.Cancel();
                    _replayTask?.Wait(TimeSpan.FromSeconds(2));
                }

                if (eventsToReplay.Any())
                {
                    // Start new replay
                    _replayTokenSource = new CancellationTokenSource();
                    _replayTask = Task.Run(async () => await ReplayEventsAsync(eventsToReplay, _replayTokenSource.Token));

                    _logger.LogInformation("Started journal replay with {Count} events from {Source}", 
                        eventsToReplay.Count, 
                        !string.IsNullOrEmpty(selectedJournalFile) ? selectedJournalFile : "recent events");
                }
            }
            
            if (!eventsToReplay.Any())
            {
                context.Response.StatusCode = 404;
                var errorMessage = !string.IsNullOrEmpty(selectedJournalFile) 
                    ? $"No events found in the last 5 minutes of journal file: {selectedJournalFile}"
                    : "No events found in the last 5 minutes";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = errorMessage }));
                return;
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new 
            { 
                success = true,
                message = $"Journal replay started from {(!string.IsNullOrEmpty(selectedJournalFile) ? Path.GetFileName(selectedJournalFile) : "recent events")}",
                events_count = eventsToReplay.Count,
                source = !string.IsNullOrEmpty(selectedJournalFile) ? Path.GetFileName(selectedJournalFile) : "recent_events"
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting journal replay");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    public async Task StopJournalReplay(HttpContext context)
    {
        try
        {
            lock (ReplayLock)
            {
                if (_replayTokenSource != null && !_replayTokenSource.Token.IsCancellationRequested)
                {
                    _replayTokenSource.Cancel();
                    _logger.LogInformation("Stopped journal replay");
                }
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new 
            { 
                success = true,
                message = "Journal replay stopped"
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping journal replay");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    public async Task GetJournalReplayStatus(HttpContext context)
    {
        try
        {
            bool isReplaying = false;
            int eventsCount = 0;

            lock (ReplayLock)
            {
                isReplaying = _replayTokenSource != null && 
                             !_replayTokenSource.Token.IsCancellationRequested &&
                             _replayTask != null && 
                             !_replayTask.IsCompleted;
                eventsCount = GetEventsToReplayCount();
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new 
            { 
                is_replaying = isReplaying,
                events_available = eventsCount,
                last_5_minutes_events = GetEventsInLast5Minutes()
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting replay status");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    private async Task ReplayEventsAsync(List<JournalEvent> events, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting replay of {Count} journal events", events.Count);
            
            foreach (var journalEvent in events)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Process the event through the normal event mapping system
                await Task.Run(() => _eventMappingService.ProcessEvent(journalEvent), cancellationToken);
                
                _logger.LogDebug("Replayed event: {EventType} at {Timestamp}", journalEvent.Event, journalEvent.Timestamp);

                // Add a small delay between events to make it more realistic
                await Task.Delay(500, cancellationToken);
            }
            
            _logger.LogInformation("Journal replay completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Journal replay was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during journal replay");
        }
    }

    private int GetEventsToReplayCount()
    {
        var cutoffTime = DateTime.UtcNow.AddMinutes(-5);
        lock (EventsLock)
        {
            return RecentEvents.Count(e => e.Timestamp >= cutoffTime);
        }
    }

    private int GetEventsInLast5Minutes()
    {
        var cutoffTime = DateTime.UtcNow.AddMinutes(-5);
        lock (EventsLock)
        {
            return RecentEvents.Count(e => e.Timestamp >= cutoffTime);
        }
    }

    private async Task<List<JournalEvent>> ReadLastFiveMinutesFromJournalFile(string journalFileName)
    {
        var events = new List<JournalEvent>();
        
        try
        {
            var journalPath = _settings.EliteDangerous.JournalPath;
            if (string.IsNullOrEmpty(journalPath) || !Directory.Exists(journalPath))
            {
                _logger.LogWarning("Journal path not configured or does not exist");
                return events;
            }

            var fullPath = Path.Combine(journalPath, journalFileName);
            if (!File.Exists(fullPath))
            {
                _logger.LogWarning("Journal file not found: {FilePath}", fullPath);
                return events;
            }

            var allLines = await File.ReadAllLinesAsync(fullPath);
            var allEvents = new List<JournalEvent>();

            // Parse all events from the journal file
            foreach (var line in allLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var journalEvent = JsonSerializer.Deserialize<JournalEvent>(line);
                    if (journalEvent != null)
                    {
                        allEvents.Add(journalEvent);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug("Failed to parse journal line: {Line} - {Error}", line, ex.Message);
                }
            }

            if (!allEvents.Any())
            {
                _logger.LogWarning("No valid events found in journal file: {FilePath}", fullPath);
                return events;
            }

            // Sort events by timestamp
            allEvents = allEvents.OrderBy(e => e.Timestamp).ToList();
            
            // Find the last event timestamp and calculate 5 minutes before that
            var lastEventTime = allEvents.Last().Timestamp;
            var cutoffTime = lastEventTime.AddMinutes(-5);

            // Get events from the last 5 minutes of the journal's timeline
            events = allEvents
                .Where(e => e.Timestamp >= cutoffTime)
                .OrderBy(e => e.Timestamp)
                .ToList();

            _logger.LogInformation("Loaded {EventCount} events from last 5 minutes of journal {FileName} (from {StartTime} to {EndTime})",
                events.Count, journalFileName, cutoffTime, lastEventTime);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading journal file: {FileName}", journalFileName);
        }

        return events;
    }
}