using EDButtkicker.Configuration;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Pattern conditions exercised through the public conditional-playback entry point, with the audio
/// engine subclassed to record what would have been played so no device is opened. Covers the
/// implemented threshold conditions - health_below, health_above, ship_type and hull_damage_above,
/// each including the case where the event carries no such field - alongside the stubs described
/// below.
///
/// The "event_frequency" and "session_duration" pattern conditions are unimplemented stubs:
/// EvaluateEventFrequency and EvaluateSessionDuration ignore their arguments and return true, so a
/// pattern gated on either one always plays. These tests pin that pass-through down through the
/// public conditional-playback entry point - the same way the in_combat tests do, with the audio
/// engine subclassed to record what would have been played so no device is opened - and are
/// expected to turn red when issue #93 replaces the stubs with real frequency and session-duration
/// tracking. When that happens the tests should be rewritten against the new behaviour, not deleted.
/// </summary>
public class PatternSequencerConditionTests : IDisposable
{
    private readonly TempDirectory _settingsDir = new("edbk-conditions");
    private readonly RecordingAudioEngine _audio;

    public PatternSequencerConditionTests()
    {
        _audio = new RecordingAudioEngine(new AppSettings());
    }

    /// <summary>
    /// Stub awaiting #93 - EvaluateEventFrequency always returns true regardless of input. A
    /// frequency of 1000 events is unreachable for a single event on a sequencer with no history,
    /// so this test will turn red when #93 replaces the stub with a real frequency check.
    /// </summary>
    [Fact]
    public async Task EventFrequency_StubAlwaysPassesThrough_CurrentBehaviour()
    {
        var sequencer = Sequencer();

        await sequencer.ExecuteConditionalPattern(PatternRequiring("event_frequency", 1000), Event("UnderAttack"));

        Assert.Single(_audio.Played);
    }

    /// <summary>
    /// Stub awaiting #93 - EvaluateSessionDuration always returns true regardless of input. A
    /// required session duration of 999999 cannot have elapsed for a sequencer constructed moments
    /// ago, so this test will turn red when #93 replaces the stub with real session tracking.
    /// </summary>
    [Fact]
    public async Task SessionDuration_StubAlwaysPassesThrough_CurrentBehaviour()
    {
        var sequencer = Sequencer();

        await sequencer.ExecuteConditionalPattern(PatternRequiring("session_duration", 999999), Event("Docked"));

        Assert.Single(_audio.Played);
    }

    /// <summary>
    /// Stub awaiting #93 - the plausible-looking value a pattern author would actually write
    /// (5 events) is accepted for the same reason an absurd one is: the value is never read.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task EventFrequency_StubIgnoresItsThreshold_CurrentBehaviour(int frequency)
    {
        var sequencer = Sequencer();

        await sequencer.ExecuteConditionalPattern(PatternRequiring("event_frequency", frequency), Event("FSDJump"));

        Assert.Single(_audio.Played);
    }

    /// <summary>
    /// Stub awaiting #93 - a 60-minute session requirement passes on a session that has just
    /// started, because EvaluateSessionDuration never inspects the requested duration.
    /// </summary>
    [Theory]
    [InlineData(60)]
    [InlineData(0)]
    [InlineData("not-a-duration")]
    public async Task SessionDuration_StubIgnoresItsThreshold_CurrentBehaviour(object duration)
    {
        var sequencer = Sequencer();

        await sequencer.ExecuteConditionalPattern(PatternRequiring("session_duration", duration), Event("FSDJump"));

        Assert.Single(_audio.Played);
    }

    /// <summary>
    /// Stub awaiting #93 - neither stub consults the contextual intelligence service, so injecting
    /// one (and putting it in a state unrelated to the pattern) changes nothing.
    /// </summary>
    [Fact]
    public async Task StubConditions_PassRegardlessOfContextualState_CurrentBehaviour()
    {
        var settings = new AppSettings();
        var userSettings = new UserSettingsService(NullLogger<UserSettingsService>.Instance, _settingsDir.Path);
        using var contextualIntelligence = new ContextualIntelligenceService(
            NullLogger<ContextualIntelligenceService>.Instance, settings, userSettings);
        contextualIntelligence.GetCurrentContext().UpdateState(GameState.Docked);
        var sequencer = Sequencer(contextualIntelligence);

        var pattern = new HapticPattern
        {
            Name = "Frequency And Duration Rumble",
            Pattern = PatternType.SharpPulse,
            Conditions = new Dictionary<string, object>
            {
                ["event_frequency"] = 5,
                ["session_duration"] = 60
            }
        };

        await sequencer.ExecuteConditionalPattern(pattern, Event("FSDJump"));

        Assert.Single(_audio.Played);
    }

    /// <summary>
    /// The stubs pass their own check but do not override the other conditions on the same pattern:
    /// conditions are ANDed, so a failing health_below still suppresses playback. This guards the
    /// surrounding AND logic that #93 will have to keep working.
    /// </summary>
    [Fact]
    public async Task StubConditions_DoNotOverrideAFailingSiblingCondition()
    {
        var sequencer = Sequencer();
        var pattern = new HapticPattern
        {
            Name = "Frequency And Health Rumble",
            Pattern = PatternType.SharpPulse,
            Conditions = new Dictionary<string, object>
            {
                ["event_frequency"] = 5,
                ["session_duration"] = 60,
                ["health_below"] = 0.2
            }
        };

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
