using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace EDButtkicker.Services;

/// <summary>
/// What the first-run wizard has actually achieved, as persisted between runs. Each step records
/// when it was confirmed and what it was confirmed with, so a half-finished setup resumes on the
/// step the user stopped at instead of starting over or pretending to be done.
/// </summary>
public class SetupState
{
    public bool Completed { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public DateTime? JournalConfirmedAtUtc { get; set; }
    public string? JournalPath { get; set; }

    public DateTime? AudioDeviceConfirmedAtUtc { get; set; }
    public string? AudioDeviceName { get; set; }

    /// <summary>Endpoint id of the confirmed output; null when the system default was chosen.</summary>
    public string? AudioDeviceEndpointId { get; set; }
    public int? AudioDeviceId { get; set; }

    public DateTime? AudioTestedAtUtc { get; set; }
    public bool? AudioTestPlayed { get; set; }
    public string? AudioTestReason { get; set; }

    public string? Version { get; set; } = "1.0.0";
}

/// <summary>
/// Stores <see cref="SetupState"/> next to the user settings. Completion is persisted so the wizard
/// does not reappear on every launch, but it is never a one-way door: <see cref="RequestReopen"/>
/// reopens the wizard for this session while leaving the completion record intact.
/// </summary>
public class SetupStateService
{
    private readonly ILogger<SetupStateService> _logger;
    private readonly string _setupStatePath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private SetupState? _cached;
    private bool _reopenRequested;

    public SetupStateService(ILogger<SetupStateService> logger)
        : this(logger, UserSettingsService.DefaultSettingsDirectory)
    {
    }

    /// <summary>
    /// Overload that puts the state file under an explicit directory, so tests never read from - or
    /// write to - the developer's real profile.
    /// </summary>
    public SetupStateService(ILogger<SetupStateService> logger, string settingsDirectory)
    {
        _logger = logger;

        if (string.IsNullOrWhiteSpace(settingsDirectory))
            throw new ArgumentException("Settings directory must be provided", nameof(settingsDirectory));

        Directory.CreateDirectory(settingsDirectory);
        _setupStatePath = Path.Combine(settingsDirectory, "setup-state.json");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            AllowTrailingCommas = true
        };

        _logger.LogDebug("SetupStateService initialized with path: {SetupStatePath}", _setupStatePath);
    }

    public string GetSetupStatePath() => _setupStatePath;

    /// <summary>True while the user has asked to see the wizard again after completing it.</summary>
    public bool ReopenRequested => Volatile.Read(ref _reopenRequested);

    public void RequestReopen()
    {
        Volatile.Write(ref _reopenRequested, true);
        _logger.LogInformation("Setup wizard reopened by request");
    }

    public async Task<SetupState> LoadAsync()
    {
        await _mutex.WaitAsync();
        try
        {
            return await LoadUnsynchronizedAsync();
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Applies <paramref name="mutate"/> to the stored state and writes it back.</summary>
    public async Task<SetupState> UpdateAsync(Action<SetupState> mutate)
    {
        await _mutex.WaitAsync();
        try
        {
            var state = await LoadUnsynchronizedAsync();
            mutate(state);
            await SaveUnsynchronizedAsync(state);
            return state;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Marks setup finished and closes a wizard that was reopened.</summary>
    public async Task<SetupState> MarkCompletedAsync(DateTime completedAtUtc)
    {
        var state = await UpdateAsync(s =>
        {
            s.Completed = true;
            s.CompletedAtUtc = completedAtUtc;
        });

        Volatile.Write(ref _reopenRequested, false);
        return state;
    }

    private async Task<SetupState> LoadUnsynchronizedAsync()
    {
        if (_cached != null)
        {
            return _cached;
        }

        if (!File.Exists(_setupStatePath))
        {
            _logger.LogInformation("No setup state found; treating this as a first run");
            _cached = new SetupState();
            return _cached;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_setupStatePath);
            _cached = JsonSerializer.Deserialize<SetupState>(json, _jsonOptions) ?? new SetupState();
        }
        catch (Exception ex)
        {
            // A damaged file must not lock the user out of the wizard - reverting to a first run
            // is the recoverable outcome.
            _logger.LogError(ex, "Error loading setup state, treating this as a first run");
            _cached = new SetupState();
        }

        return _cached;
    }

    private async Task SaveUnsynchronizedAsync(SetupState state)
    {
        _cached = state;

        try
        {
            var json = JsonSerializer.Serialize(state, _jsonOptions);
            await File.WriteAllTextAsync(_setupStatePath, json);
            _logger.LogDebug("Saved setup state to {SetupStatePath}", _setupStatePath);
        }
        catch (Exception ex)
        {
            // The in-memory state still reflects the step the user just finished; only the record
            // of it is lost, so say so instead of failing the request they made.
            _logger.LogError(ex, "Error saving setup state to {SetupStatePath}", _setupStatePath);
        }
    }
}
