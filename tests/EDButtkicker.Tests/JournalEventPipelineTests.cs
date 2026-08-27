using System.Text.Json;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Every journal event has to travel one ordered path: history, ship state, ship pattern
/// selection, then context + audio. These tests drive that pipeline through test doubles for
/// the audio and pattern-library ends - no audio device, no FileSystemWatcher, no AppData.
/// </summary>
public class JournalEventPipelineTests
{
    private static readonly DateTime Start = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Store_KeepsOnlyTheNewestThousandEvents()
    {
        var store = new JournalEventStore();

        for (var i = 0; i < 1500; i++)
        {
            store.Add(new JournalEvent { Event = $"E{i}", Timestamp = Start.AddSeconds(i) });
        }

        Assert.Equal(1000, store.Count);

        var recent = store.GetRecent(1000);
        Assert.Equal("E1499", recent[0].Event);
        Assert.Equal("E500", recent[^1].Event);
        Assert.Equal(Start.AddSeconds(1499), store.LastTimestamp);
    }

    [Fact]
    public void Store_GetRecent_IsNewestFirstAndRespectsLimit()
    {
        var store = new JournalEventStore();
        store.Add(new JournalEvent { Event = "First", Timestamp = Start });
        store.Add(new JournalEvent { Event = "Second", Timestamp = Start.AddSeconds(1) });
        store.Add(new JournalEvent { Event = "Third", Timestamp = Start.AddSeconds(2) });

        var recent = store.GetRecent(2);

        Assert.Equal(new[] { "Third", "Second" }, recent.Select(e => e.Event));
        Assert.Empty(store.GetRecent(0));
    }

    [Fact]
    public void Store_GetSince_ReturnsEventsAfterCutoffOldestFirst()
    {
        var store = new JournalEventStore();
        store.Add(new JournalEvent { Event = "Old", Timestamp = Start });
        store.Add(new JournalEvent { Event = "Recent", Timestamp = Start.AddMinutes(10) });
        store.Add(new JournalEvent { Event = "Newest", Timestamp = Start.AddMinutes(20) });

        var since = store.GetSince(Start.AddMinutes(5));

        Assert.Equal(new[] { "Recent", "Newest" }, since.Select(e => e.Event));
    }

    [Fact]
    public async Task Pipeline_RecordsHistory_TracksShip_AndFeedsAudio()
    {
        var harness = new PipelineHarness();

        await harness.Pipeline.ProcessAsync(Parse("{\"timestamp\":\"2026-08-27T12:00:00Z\",\"event\":\"LoadGame\",\"Ship\":\"python\",\"ShipName\":\"Nyx\",\"ShipID\":42}"));
        await harness.Pipeline.ProcessAsync(Parse("{\"timestamp\":\"2026-08-27T12:00:05Z\",\"event\":\"FSDJump\",\"StarSystem\":\"Shinrarta Dezhra\"}"));

        // History has both events, newest first.
        Assert.Equal(2, harness.Store.Count);
        Assert.Equal(new[] { "FSDJump", "LoadGame" }, harness.Store.GetRecent(10).Select(e => e.Event));

        // Ship state was updated, and the active pattern library follows it.
        Assert.Equal("python", harness.ShipTracking.GetCurrentShip()?.ShipType);
        Assert.Contains(harness.ShipPatterns.ShipsSet, s => s.ShipType == "python");

        // Audio ran for every event, and only after history and ship state were updated.
        Assert.Equal(new[] { "LoadGame", "FSDJump" }, harness.AudioSink.Calls.Select(c => c.Event.Event));
        Assert.Equal(new[] { 1, 2 }, harness.AudioSink.Calls.Select(c => c.StoreCountAtCall));
        Assert.Equal(new[] { "python", "python" }, harness.AudioSink.Calls.Select(c => c.ShipAtCall?.ShipType));
    }

    [Fact]
    public async Task Pipeline_PassesShipSpecificPatternToAudio()
    {
        var harness = new PipelineHarness();
        var shipPattern = new HapticPattern
        {
            Name = "Python FSD Jump",
            Pattern = PatternType.BuildupRumble,
            Frequency = 35,
            Intensity = 70,
            Duration = 3000
        };
        harness.ShipPatterns.Patterns["FSDJump"] = shipPattern;

        await harness.Pipeline.ProcessAsync(Parse("{\"timestamp\":\"2026-08-27T12:00:05Z\",\"event\":\"FSDJump\",\"StarSystem\":\"Sol\"}"));
        await harness.Pipeline.ProcessAsync(Parse("{\"timestamp\":\"2026-08-27T12:00:06Z\",\"event\":\"Docked\",\"StationName\":\"Jameson Memorial\"}"));

        Assert.Same(shipPattern, harness.AudioSink.Calls[0].Pattern);
        Assert.Null(harness.AudioSink.Calls[1].Pattern);
    }

    [Fact]
    public async Task Pipeline_SkipHistory_StillPlaysButDoesNotGrowTheStore()
    {
        var harness = new PipelineHarness();
        var replayed = Parse("{\"timestamp\":\"2026-08-27T12:00:05Z\",\"event\":\"FSDJump\",\"StarSystem\":\"Sol\"}");

        await harness.Pipeline.ProcessAsync(replayed, skipHistory: true);

        Assert.Equal(0, harness.Store.Count);
        Assert.Single(harness.AudioSink.Calls);
    }

    [Fact]
    public async Task Pipeline_IgnoresEventsWithoutAnEventName()
    {
        var harness = new PipelineHarness();

        await harness.Pipeline.ProcessAsync(new JournalEvent { Timestamp = Start });

        Assert.Equal(0, harness.Store.Count);
        Assert.Empty(harness.AudioSink.Calls);
    }

    [Fact]
    public void Reconciler_BuildsSourcesForEveryShipTypeInTheCatalog()
    {
        // Ship types deliberately outside the old hardcoded "known ship types" list.
        var catalog = new StubPatternCatalog();
        catalog.Add("diamondbackexplorer", "Explorer Pack", "FSDJump", "Touchdown");
        catalog.Add("mamba", "Combat Pack", "HullDamage");

        var sources = PatternSourceCatalogReconciler.BuildSources(catalog);

        Assert.Equal(new[] { "diamondbackexplorer", "mamba" }, catalog.ShipTypesQueried);
        Assert.Equal(3, sources.Count);
        Assert.Contains(sources, s => s.ShipType == "diamondbackexplorer" && s.EventName == "Touchdown");
        Assert.Contains(sources, s => s.ShipType == "mamba" && s.EventName == "HullDamage");
        Assert.All(sources, s => Assert.Equal(PatternSourceType.FileSystem, s.SourceInfo.SourceType));

        // Source ids are stable and unique per pack/ship/event.
        Assert.Equal(sources.Count, sources.Select(s => s.SourceInfo.SourceId).Distinct().Count());
        Assert.Contains(sources, s => s.SourceInfo.SourceId == "filesystem:combat pack:mamba:hulldamage");
    }

    [Fact]
    public void Reconciler_BuildsNothingForAnEmptyCatalog()
    {
        Assert.Empty(PatternSourceCatalogReconciler.BuildSources(new StubPatternCatalog()));
    }

    private static JournalEvent Parse(string line)
    {
        return JsonSerializer.Deserialize<JournalEvent>(line, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;
    }

    private sealed class PipelineHarness
    {
        public JournalEventStore Store { get; } = new();
        public ShipTrackingService ShipTracking { get; } = new(NullLogger<ShipTrackingService>.Instance);
        public FakeShipPatternProvider ShipPatterns { get; } = new();
        public RecordingAudioSink AudioSink { get; }
        public JournalEventPipeline Pipeline { get; }

        public PipelineHarness()
        {
            AudioSink = new RecordingAudioSink(Store, ShipTracking);
            Pipeline = new JournalEventPipeline(
                NullLogger<JournalEventPipeline>.Instance,
                Store,
                ShipTracking,
                ShipPatterns,
                AudioSink);
        }
    }

    private sealed record AudioCall(JournalEvent Event, HapticPattern? Pattern, int StoreCountAtCall, CurrentShip? ShipAtCall);

    private sealed class RecordingAudioSink : IJournalEventAudioSink
    {
        private readonly IJournalEventStore _store;
        private readonly ShipTrackingService _shipTracking;

        public List<AudioCall> Calls { get; } = new();

        public RecordingAudioSink(IJournalEventStore store, ShipTrackingService shipTracking)
        {
            _store = store;
            _shipTracking = shipTracking;
        }

        public Task ProcessEvent(JournalEvent journalEvent, HapticPattern? preferredPattern)
        {
            Calls.Add(new AudioCall(journalEvent, preferredPattern, _store.Count, _shipTracking.GetCurrentShip()));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeShipPatternProvider : IShipPatternProvider
    {
        public List<CurrentShip> ShipsSet { get; } = new();
        public Dictionary<string, HapticPattern> Patterns { get; } = new();

        public void SetCurrentShip(CurrentShip ship) => ShipsSet.Add(ship);

        public HapticPattern? GetPatternForEvent(string eventName) =>
            Patterns.TryGetValue(eventName, out var pattern) ? pattern : null;
    }

    private sealed class StubPatternCatalog : IPatternCatalog
    {
        private readonly Dictionary<string, List<ShipPatternDefinition>> _ships = new();

        public List<string> ShipTypesQueried { get; } = new();

        public void Add(string shipType, string packName, params string[] eventNames)
        {
            var definition = new ShipPatternDefinition
            {
                ShipType = shipType,
                DisplayName = shipType,
                PackName = packName,
                Author = "Tests",
                Version = "1.0.0",
                Events = eventNames.ToDictionary(
                    name => name,
                    name => new HapticPattern { Name = $"{shipType} {name}", Pattern = PatternType.Impact })
            };

            if (!_ships.TryGetValue(shipType, out var definitions))
            {
                definitions = new List<ShipPatternDefinition>();
                _ships[shipType] = definitions;
            }

            definitions.Add(definition);
        }

        public List<string> GetAllShipTypes() => _ships.Keys.OrderBy(k => k).ToList();

        public List<ShipPatternDefinition> GetPatternsForShip(string shipType)
        {
            ShipTypesQueried.Add(shipType);
            return _ships.TryGetValue(shipType, out var definitions)
                ? new List<ShipPatternDefinition>(definitions)
                : new List<ShipPatternDefinition>();
        }
    }
}
