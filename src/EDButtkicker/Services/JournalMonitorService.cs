using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using EDButtkicker.Configuration;
using EDButtkicker.Models;

namespace EDButtkicker.Services;

public class JournalMonitorService : BackgroundService
{
    private static readonly TimeSpan RotationCheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long to wait before looking at an unusable journal folder again.</summary>
    private static readonly TimeSpan FolderRecheckInterval = TimeSpan.FromSeconds(5);

    private readonly ILogger<JournalMonitorService> _logger;
    private readonly AppSettings _settings;
    private readonly JournalMonitorStatus _status;
    private readonly IJournalEventPipeline _pipeline;
    private readonly ShipTrackingService _shipTrackingService;
    private readonly ShipPatternService _shipPatternService;
    private readonly PatternSelectionService _patternSelectionService;
    private readonly PatternFileService _patternFileService;
    private readonly PatternSourceCatalogReconciler _catalogReconciler;
    private FileSystemWatcher? _fileWatcher;
    private JournalSignalPump? _pump;
    private JournalTailReader? _reader;
    private bool _subscribed;

    public event Action<JournalEvent>? JournalEventReceived;

    public JournalMonitorService(
        ILogger<JournalMonitorService> logger,
        AppSettings settings,
        JournalMonitorStatus status,
        IJournalEventPipeline pipeline,
        ShipTrackingService shipTrackingService,
        ShipPatternService shipPatternService,
        PatternSelectionService patternSelectionService,
        PatternFileService patternFileService,
        PatternSourceCatalogReconciler catalogReconciler)
    {
        _logger = logger;
        _settings = settings;
        _status = status;
        _pipeline = pipeline;
        _shipTrackingService = shipTrackingService;
        _shipPatternService = shipPatternService;
        _patternSelectionService = patternSelectionService;
        _patternFileService = patternFileService;
        _catalogReconciler = catalogReconciler;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Journal Monitor Service");

        // Saved ship libraries and pattern selections must be in memory before the first
        // journal event is processed, otherwise early events use default patterns only.
        await LoadPatternStateAsync();

        try
        {
            // A folder that does not exist yet is not fatal any more: the setup wizard can point us
            // somewhere else, and Elite Dangerous creates the folder on its first run. The monitor
            // keeps re-checking (and re-checks immediately when the health retry asks it to) instead
            // of giving up for the lifetime of the process.
            while (!stoppingToken.IsCancellationRequested)
            {
                var journalPath = _settings.EliteDangerous.JournalPath;

                if (string.IsNullOrWhiteSpace(journalPath) || !Directory.Exists(journalPath))
                {
                    var reason = string.IsNullOrWhiteSpace(journalPath)
                        ? "No journal folder is configured yet."
                        : $"The journal folder does not exist yet: {journalPath}";

                    _status.ReportWaiting(journalPath, reason);
                    _logger.LogWarning("Journal folder unavailable, waiting: {Reason}", reason);

                    await _status.WaitForRecheckAsync(FolderRecheckInterval, stoppingToken);
                    continue;
                }

                await StartMonitoring(journalPath, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _status.ReportFaulted($"Journal monitoring stopped after an error: {ex.Message}");
            throw;
        }

        _status.ReportStopped("Journal monitoring stopped.");
    }

    private async Task LoadPatternStateAsync()
    {
        try
        {
            await _shipPatternService.LoadShipPatternsAsync();
            await _patternSelectionService.LoadSelectionsAsync();
            await _catalogReconciler.ReconcileAsync();

            if (!_subscribed)
            {
                // Ship swaps keep the active library current even if a pipeline step is skipped,
                // and new/edited pattern files re-enter the selection catalog automatically.
                _shipTrackingService.ShipChanged += OnShipChanged;
                _patternFileService.PatternFilesChanged += OnPatternFilesChanged;
                _subscribed = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading ship patterns and selections; continuing with defaults");
        }
    }

    private void OnShipChanged(CurrentShip ship)
    {
        try
        {
            _shipPatternService.SetCurrentShip(ship);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying ship change to pattern library");
        }
    }

    private void OnPatternFilesChanged(PatternFileChangeEventArgs args)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _catalogReconciler.ReconcileAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reconciling pattern sources after catalog change");
            }
        });
    }

    private async Task StartMonitoring(string journalPath, CancellationToken stoppingToken)
    {
        _reader = new JournalTailReader(journalPath, _settings.EliteDangerous.MonitorLatestOnly, _logger);
        _pump = new JournalSignalPump(DrainJournalAsync, _logger);

        var latestFile = _reader.FindLatestJournalFile();
        if (latestFile == null)
        {
            _logger.LogWarning("No journal files found in {Path}; waiting for one to appear", journalPath);
        }

        // Watcher signals and the periodic rotation check both feed the same single-reader queue,
        // so overlapping Changed callbacks can never run two reads (or two cursor updates) at once.
        SetupFileWatcher(journalPath);
        _status.ReportWatching(journalPath, latestFile == null ? null : Path.GetFileName(latestFile));

        var pumpTask = _pump.RunAsync(stoppingToken);

        try
        {
            // Kick off an initial pass so we attach to the current journal immediately.
            _pump.Signal();

            while (!stoppingToken.IsCancellationRequested)
            {
                // The wait ends early when a health retry asks for a re-check, so "Retry" acts now
                // rather than at the end of the next rotation interval.
                await _status.WaitForRecheckAsync(RotationCheckInterval, stoppingToken);

                if (!Directory.Exists(journalPath) || !IsStillConfigured(journalPath))
                {
                    // The folder went away, or setup pointed us at a different one: hand back to the
                    // outer loop, which reports why and re-attaches.
                    break;
                }

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
            _pump = null;
            _reader = null;
            await pumpTask.ConfigureAwait(false);
        }
    }

    private bool IsStillConfigured(string journalPath) =>
        string.Equals(_settings.EliteDangerous.JournalPath, journalPath, StringComparison.OrdinalIgnoreCase);

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
        var reader = _reader;
        if (reader == null)
            return;

        var lines = await reader.ReadNewLinesAsync(cancellationToken).ConfigureAwait(false);

        // Which file the watcher is on is part of the health story: "attached, no journal file yet"
        // and "reading Journal.2026-08-28.log" are different states. The cursor goes with it, so
        // status shows how far into that file the reader has committed after every drain.
        var currentFile = reader.CurrentFile;
        _status.ReportWatching(
            _settings.EliteDangerous.JournalPath,
            currentFile == null ? null : Path.GetFileName(currentFile),
            currentFile == null ? null : reader.Cursor);

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessJournalLine(line);
        }

        if (lines.Count > 0)
        {
            _status.ReportLinesRead();
            _logger.LogDebug("Processed {Count} new journal entries", lines.Count);
        }
    }

    private async Task ProcessJournalLine(string line)
    {
        try
        {
            // A malformed or truncated line is skipped and logged by the parser; monitoring stays up.
            if (!JournalEventParser.TryParse(line, out var journalEvent, _logger) || journalEvent == null)
                return;

            _logger.LogDebug("Journal Event: {Event} at {Timestamp}", journalEvent.Event, journalEvent.Timestamp);

            // Raise event for subscribers
            JournalEventReceived?.Invoke(journalEvent);

            // History -> ship state -> ship pattern selection -> context + audio
            await _pipeline.ProcessAsync(journalEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing journal line");
        }
    }

    public override void Dispose()
    {
        if (_subscribed)
        {
            _shipTrackingService.ShipChanged -= OnShipChanged;
            _patternFileService.PatternFilesChanged -= OnPatternFilesChanged;
            _subscribed = false;
        }

        _fileWatcher?.Dispose();
        _pump?.Dispose();
        base.Dispose();
    }
}
