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
    private readonly IJournalEventStore _eventStore;
    private readonly IJournalEventPipeline _pipeline;
    private readonly JournalMonitorStatus _monitorStatus;

    // Replay functionality
    private CancellationTokenSource? _replayTokenSource;
    private Task? _replayTask;
    private readonly object ReplayLock = new object();

    public JournalApiController(
        ILogger<JournalApiController> logger,
        AppSettings settings,
        IJournalEventStore eventStore,
        IJournalEventPipeline pipeline,
        JournalMonitorStatus monitorStatus)
    {
        _logger = logger;
        _settings = settings;
        _eventStore = eventStore;
        _pipeline = pipeline;
        _monitorStatus = monitorStatus;
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
                    // Same glob the replay guard resolves against, so the names offered here are
                    // exactly the names replay will accept.
                    journalFiles = Directory.GetFiles(journalPath, JournalFileGuard.JournalGlob)
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

            // Monitoring means a watcher is attached, not merely that the folder is there: the two
            // came apart whenever the folder existed but the monitor could not start on it.
            var monitor = _monitorStatus.Current;
            var monitoring = monitor.State == JournalWatchState.Watching;

            var status = new
            {
                journal_path = journalPath,
                path_exists = pathExists,
                monitoring,
                monitor_state = monitor.State.ToString(),
                monitor_reason = monitor.Reason,
                monitor_active_file = monitor.ActiveFile,
                monitor_offset = monitor.Offset,
                monitor_latest_only = _settings.EliteDangerous.MonitorLatestOnly,
                recent_files = journalFiles,
                events_processed = GetRecentEventsCount(),
                last_event_time = GetLastEventTime(),
                status = monitoring ? "Connected" : "Disconnected",
                health = monitoring ? "Healthy" : "Configuration Required"
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

            // The live watcher is still attached to the old folder; ask it to re-check now so the
            // new path takes effect without a restart, the same way the setup wizard does.
            _monitorStatus.RequestRecheck();

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

            var events = _eventStore.GetRecent(limit)
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

            var response = new
            {
                events = events,
                metadata = new
                {
                    total_events = events.Count,
                    limit_applied = limit,
                    // Same meaning as the journal status endpoint: a watcher is attached, not just
                    // that the configured folder happens to exist.
                    monitoring = _monitorStatus.Current.State == JournalWatchState.Watching
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

    private int GetRecentEventsCount() => _eventStore.Count;

    private DateTime? GetLastEventTime() => _eventStore.LastTimestamp;

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

            // A requested file is only ever one of the journal files the server enumerated; anything
            // else is refused here, before a path exists, so no file outside the folder is read.
            string? sourceFile = null;
            string? resolvedPath = null;
            if (!string.IsNullOrEmpty(selectedJournalFile))
            {
                resolvedPath = JournalFileGuard.Resolve(
                    _settings.EliteDangerous.JournalPath, selectedJournalFile);

                if (resolvedPath == null)
                {
                    _logger.LogWarning(
                        "Rejected journal replay for a file that is not one of the {Glob} files in the configured journal folder",
                        JournalFileGuard.JournalGlob);

                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        error = "Journal file must be one of the journal files listed by the journal status API"
                    }));
                    return;
                }

                sourceFile = Path.GetFileName(resolvedPath);
            }

            // Get events from the selected journal file or fallback to recent events (outside of lock)
            List<JournalEvent> eventsToReplay;
            if (resolvedPath != null)
            {
                eventsToReplay = await ReadLastFiveMinutesFromJournalFile(resolvedPath);
            }
            else
            {
                // Fallback to recent events from memory (last 5 minutes of real time)
                var cutoffTime = DateTime.UtcNow.AddMinutes(-5);
                eventsToReplay = _eventStore.GetSince(cutoffTime).ToList();
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
                        sourceFile ?? "recent events");
                }
            }
            
            if (!eventsToReplay.Any())
            {
                context.Response.StatusCode = 404;
                var errorMessage = sourceFile != null
                    ? $"No events found in the last 5 minutes of journal file: {sourceFile}"
                    : "No events found in the last 5 minutes";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = errorMessage }));
                return;
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new 
            { 
                success = true,
                message = $"Journal replay started from {sourceFile ?? "recent events"}",
                events_count = eventsToReplay.Count,
                source = sourceFile ?? "recent_events"
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

                // Same ordered pipeline as live monitoring, minus the history write - these
                // events are historical and (for the in-memory source) already in the store.
                await _pipeline.ProcessAsync(journalEvent, skipHistory: true);
                
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

    private int GetEventsToReplayCount() => GetEventsInLast5Minutes();

    private int GetEventsInLast5Minutes()
    {
        var cutoffTime = DateTime.UtcNow.AddMinutes(-5);
        return _eventStore.GetSince(cutoffTime).Count;
    }

    /// <summary>
    /// Reads a journal file that <see cref="JournalFileGuard.Resolve"/> has already resolved to a
    /// full path inside the configured journal folder. Never call this with a caller-supplied name.
    /// </summary>
    private async Task<List<JournalEvent>> ReadLastFiveMinutesFromJournalFile(string fullPath)
    {
        var events = new List<JournalEvent>();
        var journalFileName = Path.GetFileName(fullPath);

        try
        {
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