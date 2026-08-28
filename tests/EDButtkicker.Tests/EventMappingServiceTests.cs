using EDButtkicker.Configuration;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Event mapping decisions - which journal events reach playback, which are dropped, and how the
/// per-event rate limit behaves over time. The audio engine is subclassed to record what would be
/// played, so no device is opened; the clock is hand-advanced, so no test waits on real time.
/// </summary>
public class EventMappingServiceTests : IDisposable
{
    private const string LimitedEvent = "UnderAttack";   // 300ms window
    private const string UnlimitedEvent = "Docked";      // no window

    private readonly TempDirectory _settingsDir = new("edbk-mapping");
    private readonly ManualTimeProvider _clock = new();
    private readonly RecordingAudioEngine _audio;
    private readonly ContextualIntelligenceService _contextualIntelligence;
    private readonly EventMappingService _service;

    public EventMappingServiceTests()
    {
        var settings = new AppSettings();
        _audio = new RecordingAudioEngine(settings);
        var userSettings = new UserSettingsService(NullLogger<UserSettingsService>.Instance, _settingsDir.Path);
        _contextualIntelligence = new ContextualIntelligenceService(
            NullLogger<ContextualIntelligenceService>.Instance, settings, userSettings);
        var sequencer = new PatternSequencer(NullLogger<PatternSequencer>.Instance, _audio, _contextualIntelligence);

        _service = new EventMappingService(
            NullLogger<EventMappingService>.Instance, _audio, sequencer, _contextualIntelligence, _clock);
    }

    [Fact]
    public async Task MappedEvent_IsPlayedWithItsMappedPattern()
    {
        _service.UpdateEventMappings(Mappings(Mapping(UnlimitedEvent, "Docking Thump", intensity: 55)));

        await _service.ProcessEvent(Event(UnlimitedEvent));

        var played = Assert.Single(_audio.Played);
        Assert.Equal("Docking Thump", played.Pattern.Name);
    }

    [Fact]
    public async Task UnmappedEvent_IsNotPlayed()
    {
        _service.UpdateEventMappings(Mappings(Mapping(UnlimitedEvent, "Docking Thump")));

        await _service.ProcessEvent(Event("SomethingNobodyMapped"));

        Assert.Empty(_audio.Played);
    }

    [Fact]
    public async Task DisabledMapping_IsNotPlayed()
    {
        _service.UpdateEventMappings(Mappings(Mapping(UnlimitedEvent, "Docking Thump", enabled: false)));

        await _service.ProcessEvent(Event(UnlimitedEvent));

        Assert.Empty(_audio.Played);
    }

    [Fact]
    public async Task EventWithoutAName_IsIgnored()
    {
        await _service.ProcessEvent(new JournalEvent { Event = string.Empty });

        Assert.Empty(_audio.Played);
    }

    [Fact]
    public async Task RepeatedEventInsideTheRateLimitWindow_IsPlayedOnce()
    {
        _service.UpdateEventMappings(Mappings(Mapping(LimitedEvent, "Incoming Fire")));

        await _service.ProcessEvent(Event(LimitedEvent));

        _clock.Advance(TimeSpan.FromMilliseconds(100));
        await _service.ProcessEvent(Event(LimitedEvent));
        _clock.Advance(TimeSpan.FromMilliseconds(100));
        await _service.ProcessEvent(Event(LimitedEvent));

        Assert.Single(_audio.Played);

        // 300ms after the accepted event the window has passed.
        _clock.Advance(TimeSpan.FromMilliseconds(100));
        await _service.ProcessEvent(Event(LimitedEvent));

        Assert.Equal(2, _audio.Played.Count);
    }

    [Fact]
    public async Task UnlimitedEventType_IsPlayedEveryTime()
    {
        _service.UpdateEventMappings(Mappings(Mapping(UnlimitedEvent, "Docking Thump")));

        for (var i = 0; i < 5; i++)
        {
            await _service.ProcessEvent(Event(UnlimitedEvent));
        }

        Assert.Equal(5, _audio.Played.Count);
    }

    [Fact]
    public async Task RateLimit_DoesNotDelayADifferentEventType()
    {
        _service.UpdateEventMappings(Mappings(
            Mapping(LimitedEvent, "Incoming Fire"),
            Mapping(UnlimitedEvent, "Docking Thump")));

        await _service.ProcessEvent(Event(LimitedEvent));
        await _service.ProcessEvent(Event(LimitedEvent));   // refused
        await _service.ProcessEvent(Event(UnlimitedEvent)); // unaffected

        Assert.Equal(new[] { "Incoming Fire", "Docking Thump" }, _audio.Played.Select(p => p.Pattern.Name));
    }

    [Fact]
    public async Task Statistics_CountOnlyThePlayedEvents()
    {
        _service.UpdateEventMappings(Mappings(Mapping(LimitedEvent, "Incoming Fire")));

        await _service.ProcessEvent(Event(LimitedEvent));
        await _service.ProcessEvent(Event(LimitedEvent)); // rate limited

        _clock.Advance(TimeSpan.FromSeconds(1));
        await _service.ProcessEvent(Event(LimitedEvent));

        var statistics = _service.GetEventStatistics();
        Assert.Equal(2, statistics[LimitedEvent]);
    }

    [Fact]
    public async Task ResetStatistics_ClearsCountsAndRateLimitWindows()
    {
        _service.UpdateEventMappings(Mappings(Mapping(LimitedEvent, "Incoming Fire")));

        await _service.ProcessEvent(Event(LimitedEvent));
        _service.ResetStatistics();

        // Same instant as the previous event: only a cleared window lets this one through.
        await _service.ProcessEvent(Event(LimitedEvent));

        Assert.Equal(2, _audio.Played.Count);
        Assert.Equal(1, _service.GetEventStatistics()[LimitedEvent]);
    }

    [Fact]
    public async Task PreferredPattern_OverridesTheMappingButKeepsRateLimiting()
    {
        _service.UpdateEventMappings(Mappings(Mapping(LimitedEvent, "Default Fire")));
        var shipPattern = Pattern("Anaconda Fire", intensity: 90);

        await _service.ProcessEvent(Event(LimitedEvent), shipPattern);
        await _service.ProcessEvent(Event(LimitedEvent), shipPattern); // inside the window

        var played = Assert.Single(_audio.Played);
        Assert.Equal("Anaconda Fire", played.Pattern.Name);
    }

    [Fact]
    public async Task PreferredPattern_IsPlayedEvenWithoutAMapping()
    {
        _service.UpdateEventMappings(Mappings()); // nothing mapped at all

        await _service.ProcessEvent(Event(UnlimitedEvent), Pattern("Ship Specific", intensity: 70));

        var played = Assert.Single(_audio.Played);
        Assert.Equal("Ship Specific", played.Pattern.Name);
    }

    [Fact]
    public async Task PlayedPattern_IsACopy_SoTheStoredMappingKeepsItsDefaults()
    {
        var mappings = Mappings(Mapping("UnderAttack", "Incoming Fire", intensity: 50));
        _service.UpdateEventMappings(mappings);

        await _service.ProcessEvent(Event("UnderAttack"));

        var played = Assert.Single(_audio.Played);
        // UnderAttack adds +10 to the event copy; the stored mapping must be untouched.
        Assert.Equal(60, played.Pattern.Intensity);
        Assert.Equal(50, mappings.EventMappings["UnderAttack"].Pattern.Intensity);
        Assert.Equal(50, _service.GetDefaultPatternForEvent("UnderAttack")!.Intensity);
    }

    [Fact]
    public void DefaultMappings_AreLoadedAtConstruction()
    {
        // The service starts from the built-in catalogue, before any file is loaded.
        Assert.NotEmpty(_service.GetAllDefaultPatterns());
        Assert.NotNull(_service.GetDefaultPatternForEvent("HullDamage"));
        Assert.Null(_service.GetDefaultPatternForEvent("NoSuchEvent"));
    }

    [Fact]
    public void EventMappings_RoundTripThroughAFile()
    {
        var configPath = _settingsDir.File("event-mappings.json");
        _service.UpdateEventMappings(Mappings(Mapping(UnlimitedEvent, "Docking Thump", intensity: 63)));

        _service.SaveEventMappings(configPath);
        Assert.True(File.Exists(configPath));

        // Overwrite in memory, then load the file back over it.
        _service.UpdateEventMappings(Mappings(Mapping(UnlimitedEvent, "Replaced", intensity: 1)));
        _service.LoadEventMappings(configPath);

        var restored = _service.GetDefaultPatternForEvent(UnlimitedEvent);
        Assert.NotNull(restored);
        Assert.Equal("Docking Thump", restored!.Name);
        Assert.Equal(63, restored.Intensity);
    }

    [Fact]
    public void LoadEventMappings_FromAMissingFile_KeepsTheCurrentMappings()
    {
        _service.UpdateEventMappings(Mappings(Mapping(UnlimitedEvent, "Docking Thump")));

        _service.LoadEventMappings(_settingsDir.File("does-not-exist.json"));

        Assert.Equal("Docking Thump", _service.GetDefaultPatternForEvent(UnlimitedEvent)!.Name);
    }

    [Fact]
    public void LoadEventMappings_FromACorruptFile_KeepsTheCurrentMappings()
    {
        var configPath = _settingsDir.File("corrupt.json");
        File.WriteAllText(configPath, "{ this is not valid json");
        _service.UpdateEventMappings(Mappings(Mapping(UnlimitedEvent, "Docking Thump")));

        _service.LoadEventMappings(configPath);

        Assert.Equal("Docking Thump", _service.GetDefaultPatternForEvent(UnlimitedEvent)!.Name);
    }

    public void Dispose()
    {
        _contextualIntelligence.Dispose();
        _audio.Dispose();
        _settingsDir.Dispose();
    }

    private static JournalEvent Event(string name) => new()
    {
        Event = name,
        Timestamp = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc)
    };

    private static HapticPattern Pattern(string name, int intensity = 50) => new()
    {
        Name = name,
        Pattern = PatternType.SharpPulse,
        Frequency = 40,
        Duration = 200,
        Intensity = intensity,
        MaxIntensity = 100
    };

    private static EventMapping Mapping(string eventType, string patternName, int intensity = 50, bool enabled = true) =>
        new()
        {
            EventType = eventType,
            Pattern = Pattern(patternName, intensity),
            Enabled = enabled
        };

    private static EventMappingsConfig Mappings(params EventMapping[] mappings) => new()
    {
        EventMappings = mappings.ToDictionary(m => m.EventType)
    };

    /// <summary>Audio engine that records what it was asked to play instead of opening a device.</summary>
    private sealed class RecordingAudioEngine : AudioEngineService
    {
        public RecordingAudioEngine(AppSettings settings)
            : base(NullLogger<AudioEngineService>.Instance, settings)
        {
        }

        public List<(HapticPattern Pattern, JournalEvent? Event)> Played { get; } = new();

        public override Task PlayHapticPattern(HapticPattern pattern, JournalEvent? journalEvent = null)
        {
            lock (Played)
            {
                Played.Add((pattern, journalEvent));
            }

            return Task.CompletedTask;
        }
    }
}
