using EDButtkicker.Configuration;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The "in_combat" pattern condition. Combat is persistent state owned by the contextual
/// intelligence service, so a pattern gated on it must keep firing for as long as the player is in
/// combat - not only on the tick a combat event happens to arrive. The condition is evaluated
/// through the public conditional-playback entry point, with the audio engine subclassed to record
/// what would have been played, so no device is opened.
/// </summary>
public class PatternSequencerInCombatTests : IDisposable
{
    private readonly TempDirectory _settingsDir = new("edbk-incombat");
    private readonly RecordingAudioEngine _audio;
    private readonly ContextualIntelligenceService _contextualIntelligence;

    public PatternSequencerInCombatTests()
    {
        var settings = new AppSettings();
        _audio = new RecordingAudioEngine(settings);
        var userSettings = new UserSettingsService(NullLogger<UserSettingsService>.Instance, _settingsDir.Path);
        _contextualIntelligence = new ContextualIntelligenceService(
            NullLogger<ContextualIntelligenceService>.Instance, settings, userSettings);
    }

    [Fact]
    public async Task InCombatState_SatisfiesCondition_OnANonCombatEvent()
    {
        var sequencer = Sequencer(_contextualIntelligence);
        _contextualIntelligence.GetCurrentContext().UpdateState(GameState.InCombat);

        await sequencer.ExecuteConditionalPattern(PatternRequiring(inCombat: true), Event("FSDJump"));

        Assert.Single(_audio.Played);
    }

    [Fact]
    public async Task OutOfCombatState_FailsCondition_EvenOnACombatEvent()
    {
        var sequencer = Sequencer(_contextualIntelligence);
        _contextualIntelligence.GetCurrentContext().UpdateState(GameState.InSupercruise);

        await sequencer.ExecuteConditionalPattern(PatternRequiring(inCombat: true), Event("UnderAttack"));

        Assert.Empty(_audio.Played);
    }

    [Fact]
    public async Task OutOfCombatState_SatisfiesANegatedCondition()
    {
        var sequencer = Sequencer(_contextualIntelligence);
        _contextualIntelligence.GetCurrentContext().UpdateState(GameState.Docked);

        await sequencer.ExecuteConditionalPattern(PatternRequiring(inCombat: false), Event("Docked"));

        Assert.Single(_audio.Played);
    }

    [Fact]
    public async Task WithoutContextualIntelligence_CombatEventStillSatisfiesTheCondition()
    {
        var sequencer = Sequencer(contextualIntelligence: null);

        await sequencer.ExecuteConditionalPattern(PatternRequiring(inCombat: true), Event("UnderAttack"));

        Assert.Single(_audio.Played);
    }

    [Fact]
    public async Task WithoutContextualIntelligence_NonCombatEventFailsTheCondition()
    {
        var sequencer = Sequencer(contextualIntelligence: null);

        await sequencer.ExecuteConditionalPattern(PatternRequiring(inCombat: true), Event("FSDJump"));

        Assert.Empty(_audio.Played);
    }

    public void Dispose()
    {
        _contextualIntelligence.Dispose();
        _settingsDir.Dispose();
    }

    private PatternSequencer Sequencer(ContextualIntelligenceService? contextualIntelligence) =>
        new(NullLogger<PatternSequencer>.Instance, _audio, contextualIntelligence);

    private static HapticPattern PatternRequiring(bool inCombat) => new()
    {
        Name = "Combat Rumble",
        Pattern = PatternType.SharpPulse,
        Conditions = new Dictionary<string, object> { ["in_combat"] = inCombat }
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
