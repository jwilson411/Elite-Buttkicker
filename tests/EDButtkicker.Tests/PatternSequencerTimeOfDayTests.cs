using EDButtkicker.Configuration;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The "time_of_day" pattern condition gates playback on a "HH:mm-HH:mm" window. Its window logic
/// lives in the pure PatternSequencer.IsWithinTimeOfDay helper so it can be checked against a
/// supplied clock reading; EvaluateTimeOfDay only supplies TimeOnly.FromDateTime(DateTime.Now).
/// These tests pin down the window arithmetic directly (including the overnight wrap that issue #125
/// fixed - "22:00-06:00" used to evaluate false at every hour of the day because 06:00 &lt; 22:00) and
/// then confirm through the public conditional-playback entry point that the helper is actually what
/// gates playback, with the audio engine subclassed to record what would have been played so no
/// device is opened.
/// </summary>
public class PatternSequencerTimeOfDayTests
{
    private readonly RecordingAudioEngine _audio = new(new AppSettings());

    [Theory]
    // Normal window: end after start, no wrap.
    [InlineData("06:00-18:00", "10:00", true)]
    [InlineData("06:00-18:00", "22:00", false)]
    [InlineData("06:00-18:00", "05:59", false)]
    [InlineData("06:00-18:00", "06:00", true)]  // inclusive lower bound
    [InlineData("06:00-18:00", "18:00", true)]  // inclusive upper bound
    [InlineData("06:00-18:00", "18:01", false)]
    // Overnight window: end before start, wraps past midnight.
    [InlineData("22:00-06:00", "23:30", true)]
    [InlineData("22:00-06:00", "12:00", false)]
    [InlineData("22:00-06:00", "22:00", true)]  // inclusive lower bound
    [InlineData("22:00-06:00", "06:00", true)]  // inclusive upper bound
    [InlineData("22:00-06:00", "00:00", true)]  // midnight itself is inside the wrap
    [InlineData("22:00-06:00", "06:01", false)]
    [InlineData("22:00-06:00", "21:59", false)]
    // Zero-width window: documented as all day rather than never.
    [InlineData("06:00-06:00", "06:00", true)]
    [InlineData("06:00-06:00", "18:00", true)]
    public void IsWithinTimeOfDay_EvaluatesWindow(string range, string now, bool expected)
    {
        Assert.Equal(expected, PatternSequencer.IsWithinTimeOfDay(range, TimeOnly.Parse(now)));
    }

    /// <summary>
    /// A condition the author got wrong must not silently suppress the pattern: every unparseable
    /// shape defaults to true rather than throwing or returning false.
    /// </summary>
    [Theory]
    [InlineData("not-a-time")]
    [InlineData("not-a-time-either")]      // splits into 3 parts
    [InlineData("06:00-nonsense")]
    [InlineData("nonsense-18:00")]
    [InlineData("06:00")]                  // no separator
    [InlineData("")]
    [InlineData("-")]
    public void IsWithinTimeOfDay_MalformedRangeDefaultsToTrue(string range)
    {
        Assert.True(PatternSequencer.IsWithinTimeOfDay(range, TimeOnly.Parse("12:00")));
    }

    [Fact]
    public void IsWithinTimeOfDay_NullRangeDefaultsToTrue()
    {
        Assert.True(PatternSequencer.IsWithinTimeOfDay(null, TimeOnly.Parse("12:00")));
    }

    /// <summary>
    /// EvaluateTimeOfDay reads the local wall clock, so the end-to-end tests build their windows
    /// around whatever "now" is on the machine running them rather than hard-coding an hour.
    /// </summary>
    [Fact]
    public async Task ExecuteConditionalPattern_PlaysWhenLocalTimeIsInsideWindow()
    {
        var sequencer = Sequencer();
        var now = TimeOnly.FromDateTime(DateTime.Now);

        await sequencer.ExecuteConditionalPattern(
            PatternRequiring("time_of_day", Range(now.AddHours(-2), now.AddHours(2))),
            Event("FSDJump"));

        Assert.Single(_audio.Played);
    }

    [Fact]
    public async Task ExecuteConditionalPattern_SuppressedWhenLocalTimeIsOutsideWindow()
    {
        var sequencer = Sequencer();
        var now = TimeOnly.FromDateTime(DateTime.Now);

        await sequencer.ExecuteConditionalPattern(
            PatternRequiring("time_of_day", Range(now.AddHours(2), now.AddHours(6))),
            Event("FSDJump"));

        Assert.Empty(_audio.Played);
    }

    /// <summary>
    /// The regression from #125: an overnight window spanning the current time used to be false at
    /// every hour, so a night-only pattern never fired. The window here wraps past midnight for any
    /// "now" - it starts 12 hours back and ends 12 hours forward, less a minute - yet contains it.
    /// </summary>
    [Fact]
    public async Task ExecuteConditionalPattern_PlaysWhenOvernightWindowSpansLocalTime()
    {
        var sequencer = Sequencer();
        var now = TimeOnly.FromDateTime(DateTime.Now);
        var range = Range(now.AddHours(-12), now.AddHours(12).AddMinutes(-1));

        Assert.True(PatternSequencer.IsWithinTimeOfDay(range, now), $"{range} should wrap around {now}");

        await sequencer.ExecuteConditionalPattern(PatternRequiring("time_of_day", range), Event("UnderAttack"));

        Assert.Single(_audio.Played);
    }

    /// <summary>
    /// A malformed range reaching the sequencer defaults to true end to end - it neither throws out
    /// of ExecuteConditionalPattern nor suppresses the pattern.
    /// </summary>
    [Theory]
    [InlineData("not-a-time")]
    [InlineData("06:00-nonsense")]
    public async Task ExecuteConditionalPattern_MalformedRangePlaysWithoutThrowing(string range)
    {
        var sequencer = Sequencer();

        await sequencer.ExecuteConditionalPattern(PatternRequiring("time_of_day", range), Event("Docked"));

        Assert.Single(_audio.Played);
    }

    /// <summary>
    /// time_of_day passing its own check does not override the other conditions on the same pattern:
    /// conditions are ANDed, so a failing health_below still suppresses playback.
    /// </summary>
    [Fact]
    public async Task ExecuteConditionalPattern_InWindowDoesNotOverrideAFailingSiblingCondition()
    {
        var sequencer = Sequencer();
        var now = TimeOnly.FromDateTime(DateTime.Now);
        var pattern = new HapticPattern
        {
            Name = "Night Watch Rumble",
            Pattern = PatternType.SharpPulse,
            Conditions = new Dictionary<string, object>
            {
                ["time_of_day"] = Range(now.AddHours(-2), now.AddHours(2)),
                ["health_below"] = 0.2
            }
        };

        await sequencer.ExecuteConditionalPattern(pattern, Event("HullDamage"));

        Assert.Empty(_audio.Played);
    }

    private static string Range(TimeOnly start, TimeOnly end) => $"{start:HH:mm}-{end:HH:mm}";

    private PatternSequencer Sequencer() =>
        new(NullLogger<PatternSequencer>.Instance, _audio, null);

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
