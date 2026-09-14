using EDButtkicker.Models;

namespace EDButtkicker.Services;

/// <summary>
/// Spoken feedback, as the event pipeline asks for it. The interface deliberately carries no
/// platform attribute: the pipeline runs everywhere, so it can hold one of these without
/// System.Speech reaching into its signature. Where no speech engine exists nothing is registered
/// and callers simply have null - see <see cref="Hosting.ServiceCollectionExtensions"/>.
/// </summary>
public interface IVoiceFeedback
{
    /// <summary>Whether a speech engine was started, and an announcement would actually be heard.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Speaks <paramref name="message"/>, filling the <c>{ship}</c>, <c>{health}</c>,
    /// <c>{station}</c> and <c>{system}</c> placeholders from <paramref name="journalEvent"/>.
    /// Never throws: a speech failure is logged and swallowed, because a lost announcement must
    /// not take the haptics it came with down with it.
    /// </summary>
    Task AnnounceAsync(string message, JournalEvent? journalEvent = null);

    /// <summary>
    /// Retunes the running speech engine to <paramref name="volume"/> (0-100) and
    /// <paramref name="rate"/> (-10 to 10), so a change to either is heard on the next announcement
    /// rather than after a restart. Returns false when there is no engine to retune - off Windows,
    /// or before <c>Initialize</c> has started one - which is the caller's cue to say the value was
    /// saved but is not live yet. Never throws.
    /// </summary>
    bool ApplyVoiceSettings(int volume, int rate);
}
