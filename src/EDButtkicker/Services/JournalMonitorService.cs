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
        IJournalEventPipeline pipeline,
        ShipTrackingService shipTrackingService,
        ShipPatternService shipPatternService,
        PatternSelectionService patternSelectionService,
        PatternFileService patternFileService,
        PatternSourceCatalogReconciler catalogReconciler)
    {
        _logger = logger;
        _settings = settings;
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

        var journalPath = _settings.EliteDangerous.JournalPath;
        if (!Directory.Exists(journalPath))
        {
            _logger.LogError("Journal path does not exist: {Path}", journalPath);
            return;
        }

        await StartMonitoring(journalPath, stoppingToken);
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

            // History -> ship state -> ship pattern selection -> context + audio
            await _pipeline.ProcessAsync(journalEvent);
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
