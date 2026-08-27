using Microsoft.Extensions.Logging;
using EDButtkicker.Models;

namespace EDButtkicker.Services;

/// <summary>
/// The single ordered path every journal event takes: history, ship state, ship pattern
/// selection, then contextual intelligence + audio. Live monitoring and replay both go
/// through here so they can never drift apart.
/// </summary>
public interface IJournalEventPipeline
{
    Task ProcessAsync(JournalEvent journalEvent, bool skipHistory = false);
}

/// <summary>
/// The audio/context end of the pipeline. Implemented by <see cref="EventMappingService"/>;
/// the seam keeps the audio stack out of pipeline tests.
/// </summary>
public interface IJournalEventAudioSink
{
    Task ProcessEvent(JournalEvent journalEvent, HapticPattern? preferredPattern);
}

/// <summary>
/// The ship-specific pattern library. Implemented by <see cref="ShipPatternService"/>.
/// </summary>
public interface IShipPatternProvider
{
    void SetCurrentShip(CurrentShip ship);

    HapticPattern? GetPatternForEvent(string eventName);
}

public class JournalEventPipeline : IJournalEventPipeline
{
    private readonly ILogger<JournalEventPipeline> _logger;
    private readonly IJournalEventStore _store;
    private readonly ShipTrackingService _shipTracking;
    private readonly IShipPatternProvider _shipPatterns;
    private readonly IJournalEventAudioSink _audioSink;

    public JournalEventPipeline(
        ILogger<JournalEventPipeline> logger,
        IJournalEventStore store,
        ShipTrackingService shipTracking,
        IShipPatternProvider shipPatterns,
        IJournalEventAudioSink audioSink)
    {
        _logger = logger;
        _store = store;
        _shipTracking = shipTracking;
        _shipPatterns = shipPatterns;
        _audioSink = audioSink;
    }

    public async Task ProcessAsync(JournalEvent journalEvent, bool skipHistory = false)
    {
        if (journalEvent == null || string.IsNullOrEmpty(journalEvent.Event))
            return;

        // 1. History - skipped for replay, where the events already came from the store.
        if (!skipHistory)
        {
            _store.Add(journalEvent);
        }

        // 2. Ship state.
        _shipTracking.ProcessJournalEvent(journalEvent);

        // 3. Keep the active pattern library pointed at the ship we are actually flying.
        var currentShip = _shipTracking.GetCurrentShip();
        if (currentShip != null)
        {
            _shipPatterns.SetCurrentShip(currentShip);
        }

        // 4. Contextual intelligence + audio, preferring a ship-specific pattern when one exists.
        var shipPattern = _shipPatterns.GetPatternForEvent(journalEvent.Event);
        await _audioSink.ProcessEvent(journalEvent, shipPattern);

        _logger.LogDebug("Pipeline processed {Event} (ship: {Ship}, ship pattern: {HasPattern})",
            journalEvent.Event, currentShip?.ShipType ?? "unknown", shipPattern != null);
    }
}
