using EDButtkicker.Configuration;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>Hardware-free playback checks for pattern conditions, including session thresholds.</summary>
public class PatternSequencerConditionTests : IDisposable
{
    private readonly TempDirectory _settingsDir = new("edbk-conditions");
    private readonly RecordingAudioEngine _audio;

    public PatternSequencerConditionTests()
    {
        _audio = new RecordingAudioEngine(new AppSettings());
    }

    [Fact]
    public async Task EventFrequency_CountsOccurrencesOfTheSameEventBeyondThreshold()
    {
        var sequencer = Sequencer();
        var pattern = PatternRequiring("event_frequency", 2);
        sequencer.RecordEvent("Docked");
        sequencer.RecordEvent("FSDJump");
        await sequencer.ExecuteConditionalPattern(pattern, Event("FSDJump"));
        sequencer.RecordEvent("FSDJump");
        await sequencer.ExecuteConditionalPattern(pattern, Event("FSDJump"));
        Assert.Empty(_audio.Played);
        sequencer.RecordEvent("FSDJump");
        await sequencer.ExecuteConditionalPattern(pattern, Event("FSDJump"));
        Assert.Single(_audio.Played);
    }

    [Fact]
    public async Task EventFrequency_EvaluatingPatternsDoesNotCountExtraEvents()
    {
        var sequencer = Sequencer();
        sequencer.RecordEvent("Docked");
        for (var i = 0; i < 3; i++)
            await sequencer.ExecuteConditionalPattern(PatternRequiring("event_frequency", 1), Event("Docked"));
        Assert.Empty(_audio.Played);
    }

    [Theory]
    [InlineData("60")]
    [InlineData("PT1M")]
    public async Task SessionDuration_PlaysAtTheThreshold(string duration)
    {
        var clock = new SessionClock();
        var sequencer = new PatternSequencer(NullLogger<PatternSequencer>.Instance, _audio, null, clock);
        var pattern = PatternRequiring("session_duration", duration);
        clock.Advance(TimeSpan.FromSeconds(59));
        await sequencer.ExecuteConditionalPattern(pattern, Event("Docked"));
        Assert.Empty(_audio.Played);
        clock.Advance(TimeSpan.FromSeconds(1));
        await sequencer.ExecuteConditionalPattern(pattern, Event("Docked"));
        Assert.Single(_audio.Played);
    }

    [Theory]
    [InlineData("event_frequency", -1)]
    [InlineData("event_frequency", "NaN")]
    [InlineData("event_frequency", "Infinity")]
    [InlineData("event_frequency", "invalid")]
    [InlineData("session_duration", -1)]
    [InlineData("session_duration", "-PT1M")]
    [InlineData("session_duration", "NaN")]
    [InlineData("session_duration", "Infinity")]
    [InlineData("session_duration", "invalid")]
    [InlineData("session_duration", "P999999999999999999999D")]
    public async Task SessionConditions_InvalidThresholdsDoNotPlay(string key, object value)
    {
        var sequencer = Sequencer();
        sequencer.RecordEvent("Docked");
        await sequencer.ExecuteConditionalPattern(PatternRequiring(key, value), Event("Docked"));
        Assert.Empty(_audio.Played);
    }

    [Theory]
    [InlineData("event_frequency")]
    [InlineData("session_duration")]
    public async Task SessionConditions_ZeroThresholdPlays(string key)
    {
        var sequencer = Sequencer();
        sequencer.RecordEvent("Docked");
        await sequencer.ExecuteConditionalPattern(PatternRequiring(key, 0), Event("Docked"));
        Assert.Single(_audio.Played);
    }

    [Fact]
    public async Task SessionConditions_DeserializeNumericJsonThresholds()
    {
        var clock = new SessionClock();
        var sequencer = new PatternSequencer(NullLogger<PatternSequencer>.Instance, _audio, null, clock);
        var pattern = PatternRequiring("event_frequency", 0);
        pattern.Conditions = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
            "{\"event_frequency\": 1, \"session_duration\": 0.5}")!;
        sequencer.RecordEvent("Docked");
        sequencer.RecordEvent("Docked");
        clock.Advance(TimeSpan.FromSeconds(0.5));
        await sequencer.ExecuteConditionalPattern(pattern, Event("Docked"));
        Assert.Single(_audio.Played);
    }

    [Fact]
    public async Task SessionConditions_DoNotOverrideAFailingSiblingCondition()
    {
        var sequencer = Sequencer();
        sequencer.RecordEvent("HullDamage");
        var pattern = PatternRequiring("event_frequency", 0);
        pattern.Conditions["session_duration"] = 0;
        pattern.Conditions["health_below"] = 0.2;
        await sequencer.ExecuteConditionalPattern(pattern, Event("HullDamage"));
        Assert.Empty(_audio.Played);
    }

    /// <summary>
    /// A condition the sequencer has no branch for - a misspelt "time_of_day", say - still lets the
    /// pattern play: a typo must not cost the author their haptics, and the schema validator already
    /// refuses the pack before it can reach a release. What the runtime owes the author is a warning
    /// that names the key, so a pattern that fires at every opportunity is explicable rather than
    /// mysterious.
    /// </summary>
    [Fact]
    public async Task AnUnknownConditionKey_PlaysAnywayAndIsNamedInAWarning()
    {
        var log = new CapturingSequencerLogger();
        var sequencer = new PatternSequencer(log, _audio, null);

        await sequencer.ExecuteConditionalPattern(PatternRequiring("time_od_day", "morning"), Event("FSDJump"));

        Assert.Single(_audio.Played);
        Assert.Contains(log.Warnings, w => w.Contains("time_od_day") && w.Contains("always-true"));
    }

    /// <summary>
    /// health_below is the canonical "my hull is in trouble" gate: a pattern asking for
    /// health_below 0.5 plays at 30% health and stays silent at 80%.
    /// </summary>
    [Fact]
    public async Task HealthBelow_Plays_WhenHealthIsBelowTheThreshold()
    {
        var sequencer = Sequencer();

        await sequencer.ExecuteConditionalPattern(PatternRequiring("health_below", 0.5), EventWithHealth(0.3));

        Assert.Single(_audio.Played);
    }

    [Fact]
    public async Task HealthBelow_Suppressed_WhenHealthIsAboveTheThreshold()
    {
        var sequencer = Sequencer();

        await sequencer.ExecuteConditionalPattern(PatternRequiring("health_below", 0.5), EventWithHealth(0.8));

        Assert.Empty(_audio.Played);
    }

    /// <summary>
    /// Most journal events carry no Health field at all. EvaluateHealthBelow returns false rather
    /// than defaulting to true, so a health-gated pattern stays silent on events that say nothing
    /// about health - the opposite of the always-true default an unknown condition key gets.
    /// </summary>
    [Fact]
    public async Task HealthBelow_Suppressed_WhenTheEventCarriesNoHealth()
    {
        var sequencer = Sequencer();

        await sequencer.ExecuteConditionalPattern(PatternRequiring("health_below", 0.5), EventWithHealth(null));

        Assert.Empty(_audio.Played);
    }

    [Fact]
    public async Task HealthAbove_Plays_WhenHealthIsAboveTheThreshold()
    {
        var sequencer = Sequencer();

        await sequencer.ExecuteConditionalPattern(PatternRequiring("health_above", 0.5), EventWithHealth(0.8));

        Assert.Single(_audio.Played);
    }

    [Fact]
    public async Task HealthAbove_Suppressed_WhenHealthIsBelowTheThreshold()
    {
        var sequencer = Sequencer();

        await sequencer.ExecuteConditionalPattern(PatternRequiring("health_above", 0.5), EventWithHealth(0.3));

        Assert.Empty(_audio.Played);
    }

    /// <summary>
    /// A missing Health field suppresses health_above too: the absent-field default is false on both
    /// sides of the comparison, so neither direction quietly fires on every event.
    /// </summary>
    [Fact]
    public async Task HealthAbove_Suppressed_WhenTheEventCarriesNoHealth()
    {
        var sequencer = Sequencer();

        await sequencer.ExecuteConditionalPattern(PatternRequiring("health_above", 0.5), EventWithHealth(null));

        Assert.Empty(_audio.Played);
    }

    [Fact]
    public async Task ShipType_Plays_WhenTheShipMatches()
    {
        var sequencer = Sequencer();

        await sequencer.ExecuteConditionalPattern(
            PatternRequiring("ship_type", "sidewinder"), EventWithShip("sidewinder"));

        Assert.Single(_audio.Played);
    }

    [Fact]
    public async Task ShipType_Suppressed_WhenTheShipIsADifferentHull()
    {
        var sequencer = Sequencer();

        await sequencer.ExecuteConditionalPattern(
            PatternRequiring("ship_type", "sidewinder"), EventWithShip("eagle"));

        Assert.Empty(_audio.Played);
    }

    /// <summary>
    /// No Ship field means no match: EvaluateShipType returns false for a null or empty Ship rather
    /// than treating "we don't know which ship" as "any ship".
    /// </summary>
    [Fact]
    public async Task ShipType_Suppressed_WhenTheEventCarriesNoShip()
    {
        var sequencer = Sequencer();

        await sequencer.ExecuteConditionalPattern(
            PatternRequiring("ship_type", "sidewinder"), EventWithShip(null));

        Assert.Empty(_audio.Played);
    }

    [Fact]
    public async Task HullDamageAbove_Plays_WhenDamageExceedsTheThreshold()
    {
        var sequencer = Sequencer();

        await sequencer.ExecuteConditionalPattern(
            PatternRequiring("hull_damage_above", 0.5), EventWithHullDamage(0.8));

        Assert.Single(_audio.Played);
    }

    [Fact]
    public async Task HullDamageAbove_Suppressed_WhenDamageIsBelowTheThreshold()
    {
        var sequencer = Sequencer();

        await sequencer.ExecuteConditionalPattern(
            PatternRequiring("hull_damage_above", 0.5), EventWithHullDamage(0.3));

        Assert.Empty(_audio.Played);
    }

    /// <summary>
    /// An event with no HullDamage field suppresses the pattern - the same absent-field default the
    /// health conditions use.
    /// </summary>
    [Fact]
    public async Task HullDamageAbove_Suppressed_WhenTheEventCarriesNoHullDamage()
    {
        var sequencer = Sequencer();

        await sequencer.ExecuteConditionalPattern(
            PatternRequiring("hull_damage_above", 0.5), EventWithHullDamage(null));

        Assert.Empty(_audio.Played);
    }

    public void Dispose()
    {
        _settingsDir.Dispose();
    }

    private PatternSequencer Sequencer(ContextualIntelligenceService? contextualIntelligence = null) =>
        new(NullLogger<PatternSequencer>.Instance, _audio, contextualIntelligence);

    private static HapticPattern PatternRequiring(string condition, object value) => new()
    {
        Name = $"{condition} Rumble",
        Pattern = PatternType.SharpPulse,
        Conditions = new Dictionary<string, object> { [condition] = value }
    };

    private static JournalEvent Event(string name) => new()
    {
        Event = name,
        Timestamp = DateTime.UtcNow
    };

    /// <summary>A HullDamage event whose Health field is set - or, for null, left absent.</summary>
    private static JournalEvent EventWithHealth(double? health)
    {
        var journalEvent = Event("HullDamage");
        journalEvent.Health = health;
        return journalEvent;
    }

    /// <summary>A HullDamage event whose HullDamage field is set - or, for null, left absent.</summary>
    private static JournalEvent EventWithHullDamage(double? hullDamage)
    {
        var journalEvent = Event("HullDamage");
        journalEvent.HullDamage = hullDamage;
        return journalEvent;
    }

    /// <summary>A Loadout event whose Ship field is set - or, for null, left absent.</summary>
    private static JournalEvent EventWithShip(string? ship)
    {
        var journalEvent = Event("Loadout");
        journalEvent.Ship = ship;
        return journalEvent;
    }

    private sealed class SessionClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan delta) => _ticks += delta.Ticks;
    }

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

    /// <summary>
    /// A minimal logger that captures warning messages so tests can assert on them.
    /// Scoped to <see cref="PatternSequencer"/> so it satisfies the constructor's
    /// <c>ILogger&lt;PatternSequencer&gt;</c> parameter without pulling in the
    /// <see cref="PatternSchemaValidationTests"/>-private <c>CapturingLogger</c>.
    /// </summary>
    private sealed class CapturingSequencerLogger : ILogger<PatternSequencer>
    {
        private readonly List<string> _warnings = new();

        public IReadOnlyList<string> Warnings
        {
            get { lock (_warnings) { return _warnings.ToList(); } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                var message = formatter(state, exception);
                lock (_warnings) { _warnings.Add(message); }
            }
        }
    }
}
