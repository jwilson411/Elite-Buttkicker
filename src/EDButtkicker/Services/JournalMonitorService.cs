using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Models;

namespace EDButtkicker.Services;

public class JournalMonitorService : BackgroundService
{
    private static readonly TimeSpan RotationCheckInterval = TimeSpan.FromSeconds(1);

    private readonly ILogger<JournalMonitorService> _logger;
    private readonly AppSettings _settings;
    private readonly EventMappingService _eventMappingService;
    private readonly ShipTrackingService _shipTrackingService;
    private FileSystemWatcher? _fileWatcher;
    private JournalSignalPump? _pump;
    private JournalTailReader? _reader;

    public event Action<JournalEvent>? JournalEventReceived;

    public JournalMonitorService(
        ILogger<JournalMonitorService> logger,
        AppSettings settings,
        EventMappingService eventMappingService,
        ShipTrackingService shipTrackingService)
    {
        _logger = logger;
        _settings = settings;
        _eventMappingService = eventMappingService;
        _shipTrackingService = shipTrackingService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Journal Monitor Service");

        var journalPath = _settings.EliteDangerous.JournalPath;
        if (!Directory.Exists(journalPath))
        {
            _logger.LogError("Journal path does not exist: {Path}", journalPath);
            return;
        }

        await StartMonitoring(journalPath, stoppingToken);
    }

    private async Task StartMonitoring(string journalPath, CancellationToken stoppingToken)
    {
        _reader = new JournalTailReader(journalPath, _settings.EliteDangerous.MonitorLatestOnly, _logger);
        _pump = new JournalSignalPump(DrainJournalAsync, _logger);

        if (_reader.FindLatestJournalFile() == null)
        {
            _logger.LogWarning("No journal files found in {Path}; waiting for one to appear", journalPath);
        }

        // Watcher signals and the periodic rotation check both feed the same single-reader queue,
        // so overlapping Changed callbacks can never run two reads (or two cursor updates) at once.
        SetupFileWatcher(journalPath);

        var pumpTask = _pump.RunAsync(stoppingToken);

        try
        {
            // Kick off an initial pass so we attach to the current journal immediately.
            _pump.Signal();

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(RotationCheckInterval, stoppingToken);
                _pump.Signal();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in journal monitoring");
            throw;
        }
        finally
        {
            _fileWatcher?.Dispose();
            _fileWatcher = null;
            _pump.Dispose();
            await pumpTask.ConfigureAwait(false);
        }
    }

    private void SetupFileWatcher(string journalPath)
    {
        _fileWatcher?.Dispose();

        // Watching the directory (rather than one file) means rotation needs no watcher rebuild.
        _fileWatcher = new FileSystemWatcher(journalPath, JournalTailReader.JournalSearchPattern)
        {
            NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        _fileWatcher.Changed += (_, _) => _pump?.Signal();
        _fileWatcher.Created += (_, _) => _pump?.Signal();
        _fileWatcher.Renamed += (_, _) => _pump?.Signal();
        _fileWatcher.Error += (_, e) => _logger.LogWarning(e.GetException(), "Journal file watcher error");

        _logger.LogDebug("File watcher setup for: {Path}", journalPath);
    }

    private async Task DrainJournalAsync(CancellationToken cancellationToken)
    {
        if (_reader == null)
            return;

        var lines = await _reader.ReadNewLinesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessJournalLine(line);
        }

        if (lines.Count > 0)
        {
            _logger.LogDebug("Processed {Count} new journal entries", lines.Count);
        }
    }

    private async Task ProcessJournalLine(string line)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            var journalEvent = JsonSerializer.Deserialize<JournalEvent>(line, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (journalEvent == null)
                return;

            _logger.LogDebug("Journal Event: {Event} at {Timestamp}", journalEvent.Event, journalEvent.Timestamp);

            // Raise event for subscribers
            JournalEventReceived?.Invoke(journalEvent);

            // Process through event mapping service
            await _eventMappingService.ProcessEvent(journalEvent);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Failed to parse journal line: {Error}", ex.Message);
            _logger.LogDebug("Problematic line: {Line}", line);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing journal line");
        }
    }

    public override void Dispose()
    {
        _fileWatcher?.Dispose();
        _pump?.Dispose();
        base.Dispose();
    }
}
