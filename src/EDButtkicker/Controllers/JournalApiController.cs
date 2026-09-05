using Microsoft.AspNetCore.Http;
using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Hosting;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging;

namespace EDButtkicker.Controllers;

public class JournalApiController
{
    /// <summary>The replay window: the last five minutes of the source's own timeline.</summary>
    private static readonly TimeSpan ReplayWindow = TimeSpan.FromMinutes(5);

    private readonly ILogger<JournalApiController> _logger;
    private readonly AppSettings _settings;
    private readonly IJournalEventStore _eventStore;
    private readonly IJournalEventPipeline _pipeline;
    private readonly JournalMonitorStatus _monitorStatus;
    private readonly SettingsPersistenceService _settingsPersistence;

    // Replay functionality
    private CancellationTokenSource? _replayTokenSource;
    private Task? _replayTask;
    private readonly object ReplayLock = new object();

    public JournalApiController(
        ILogger<JournalApiController> logger,
        AppSettings settings,
        IJournalEventStore eventStore,
        IJournalEventPipeline pipeline,
        JournalMonitorStatus monitorStatus,
        SettingsPersistenceService settingsPersistence)
    {
        _logger = logger;
        _settings = settings;
        _eventStore = eventStore;
        _pipeline = pipeline;
        _monitorStatus = monitorStatus;
        _settingsPersistence = settingsPersistence;
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
            await ApiError.WriteAsync(context, 500, "Failed to read the journal status");
        }
    }

    public async Task SetJournalPath(HttpContext context)
    {
        try
        {
            var json = await BoundedRequestReader.ReadOrRespondAsync(context, "Request body is empty");
            if (json == null)
            {
                return;
            }

            if (!BoundedRequestReader.TryDeserialize<Dictionary<string, object>>(json, out var pathData))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Request body is not valid JSON" }));
                return;
            }

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

            if (journalPath.Length > RequestLimits.MaxPathLength)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    error = $"Path must not exceed {RequestLimits.MaxPathLength} characters"
                }));
                return;
            }

            // Expand environment variables the same way the persistence service will, so the folder
            // that is checked here is the folder that gets saved.
            journalPath = SettingsPersistenceService.ExpandJournalPath(journalPath);

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

            // One validated path for the change: it is applied to the running configuration, the
            // live watcher is asked to re-attach, and the folder is written to the settings file -
            // so the choice is still there after a restart.
            var result = await _settingsPersistence.ApplyAsync(new SettingsUpdate { JournalPath = journalPath });

            if (!result.Valid)
            {
                context.Response.StatusCode = 400;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    error = result.Message,
                    path = journalPath,
                    validation_errors = result.ValidationErrors
                }));
                return;
            }

            _logger.LogInformation("Journal path updated to {JournalPath}: {Message}", journalPath, result.Message);

            // Not saved means the folder is in use for this session only; that is not a success.
            context.Response.StatusCode = result.Saved ? 200 : 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                success = result.Saved,
                message = result.Message,
                path = journalPath,
                journal_files_found = journalFiles.Length,
                settings = result.ToPayload()
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting journal path");
            await ApiError.WriteAsync(context, 500, "Failed to set the journal folder");
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
            await ApiError.WriteAsync(context, 500, "Failed to read recent journal events");
        }
    }

    private int GetRecentEventsCount() => _eventStore.Count;

    private DateTime? GetLastEventTime() => _eventStore.LastTimestamp;

    public async Task StartJournalReplay(HttpContext context)
    {
        try
        {
            // Parse request body to get journal file selection. No body at all is allowed: that is
            // the "replay what is in memory" case.
            var json = await BoundedRequestReader.ReadOrRespondAsync(context, emptyBodyError: null);
            if (json == null)
            {
                return;
            }

            string? selectedJournalFile = null;
            if (json.Length > 0)
            {
                if (!BoundedRequestReader.TryDeserialize<Dictionary<string, object>>(json, out var requestData))
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Request body is not valid JSON" }));
                    return;
                }

                if (requestData != null && requestData.ContainsKey("journalFile"))
                {
                    selectedJournalFile = requestData["journalFile"].ToString();
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
            await ApiError.WriteAsync(context, 500, "Failed to start the journal replay");
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
            await ApiError.WriteAsync(context, 500, "Failed to stop the journal replay");
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
            await ApiError.WriteAsync(context, 500, "Failed to read the replay status");
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
        var journalFileName = Path.GetFileName(fullPath);

        try
        {
            // Only the tail is read: a session that has been running for hours is a large file, and
            // none of it outside the replay window is worth holding.
            var events = await JournalReplayTailReader.ReadTailAsync(fullPath, ReplayWindow, _logger);

            if (events.Count == 0)
            {
                _logger.LogWarning("No valid events found in the replay window of journal file: {FilePath}", fullPath);
                return events;
            }

            _logger.LogInformation("Loaded {EventCount} events from last 5 minutes of journal {FileName} (from {StartTime} to {EndTime})",
                events.Count, journalFileName, events[0].Timestamp, events[^1].Timestamp);

            return events;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading journal file: {FileName}", journalFileName);
            return new List<JournalEvent>();
        }
    }
}
