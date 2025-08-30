using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Models;

namespace EDButtkicker.Services;

public class JournalMonitorService : BackgroundService
{
    private readonly ILogger<JournalMonitorService> _logger;
    private readonly AppSettings _settings;
    private readonly EventMappingService _eventMappingService;
    private FileSystemWatcher? _fileWatcher;
    private string? _currentJournalFile;
    private long _lastPosition = 0;

    public event Action<JournalEvent>? JournalEventReceived;

    public JournalMonitorService(
        ILogger<JournalMonitorService> logger,
        AppSettings settings,
        EventMappingService eventMappingService)
    {
        _logger = logger;
        _settings = settings;
        _eventMappingService = eventMappingService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Journal Monitor Service");

        if (!Directory.Exists(_settings.EliteDangerous.JournalPath))
        {
            _logger.LogError("Journal path does not exist: {Path}", _settings.EliteDangerous.JournalPath);
            return;
        }

        // Find and monitor the latest journal file
        await StartMonitoring(stoppingToken);
    }

    private async Task StartMonitoring(CancellationToken stoppingToken)
    {
        try
        {
            // Find the latest journal file
            var latestJournal = FindLatestJournalFile();
            if (latestJournal == null)
            {
                _logger.LogWarning("No journal files found in {Path}", _settings.EliteDangerous.JournalPath);
                await Task.Delay(5000, stoppingToken);
                return;
            }

            _currentJournalFile = latestJournal;
            _logger.LogInformation("Monitoring journal file: {File}", Path.GetFileName(_currentJournalFile));

            // Read existing entries if monitoring latest only
            if (_settings.EliteDangerous.MonitorLatestOnly)
            {
                _lastPosition = new FileInfo(_currentJournalFile).Length;
                _logger.LogInformation("Starting from end of file (position {Position})", _lastPosition);
            }
            else
            {
                // Process entire file
                await ProcessExistingEntries(_currentJournalFile, stoppingToken);
            }

            // Set up file watcher
            SetupFileWatcher();

            // Keep service running
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
                
                // Check for new journal files periodically
                var newLatestJournal = FindLatestJournalFile();
                if (newLatestJournal != _currentJournalFile)
                {
                    _logger.LogInformation("New journal file detected: {File}", Path.GetFileName(newLatestJournal));
                    _currentJournalFile = newLatestJournal;
                    _lastPosition = 0;
                    SetupFileWatcher();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in journal monitoring");
            throw;
        }
    }

    private string? FindLatestJournalFile()
    {
        try
        {
            var journalFiles = Directory.GetFiles(_settings.EliteDangerous.JournalPath, "Journal.*.log")
                                      .OrderByDescending(f => new FileInfo(f).CreationTime)
                                      .ToList();

            return journalFiles.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding journal files");
            return null;
        }
    }

    private void SetupFileWatcher()
    {
        _fileWatcher?.Dispose();

        if (_currentJournalFile == null) return;

        var directory = Path.GetDirectoryName(_currentJournalFile)!;
        var fileName = Path.GetFileName(_currentJournalFile);

        _fileWatcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };

        _fileWatcher.Changed += async (sender, e) =>
        {
            try
            {
                await ProcessNewEntries(e.FullPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing file changes");
            }
        };

        _logger.LogDebug("File watcher setup for: {File}", fileName);
    }

    private async Task ProcessExistingEntries(string filePath, CancellationToken stoppingToken)
    {
        try
        {
            using var reader = new StreamReader(filePath);
            string? line;
            int lineCount = 0;

            while ((line = await reader.ReadLineAsync()) != null && !stoppingToken.IsCancellationRequested)
            {
                lineCount++;
                await ProcessJournalLine(line, lineCount);
            }

            _lastPosition = reader.BaseStream.Position;
            _logger.LogInformation("Processed {Count} existing journal entries", lineCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing existing entries");
        }
    }

    private async Task ProcessNewEntries(string filePath)
    {
        try
        {
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            
            if (fileStream.Length <= _lastPosition)
                return;

            fileStream.Seek(_lastPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fileStream);

            string? line;
            int newLines = 0;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                newLines++;
                await ProcessJournalLine(line, -1); // -1 indicates new entry
            }

            _lastPosition = fileStream.Position;
            
            if (newLines > 0)
            {
                _logger.LogDebug("Processed {Count} new journal entries", newLines);
            }
        }
        catch (IOException)
        {
            // File might be locked, try again later
            await Task.Delay(100);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing new entries");
        }
    }

    private async Task ProcessJournalLine(string line, int lineNumber)
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
            _logger.LogWarning("Failed to parse journal line {LineNumber}: {Error}", lineNumber, ex.Message);
            _logger.LogDebug("Problematic line: {Line}", line);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing journal line {LineNumber}", lineNumber);
        }
    }

    public override void Dispose()
    {
        _fileWatcher?.Dispose();
        base.Dispose();
    }
}