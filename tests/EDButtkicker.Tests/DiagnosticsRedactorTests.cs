using EDButtkicker.Services;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The support bundle is written to be handed to a stranger on GitHub, so the thing that makes its
/// strings safe has to be pinned directly. Every case here is something that really does appear in
/// this application's own text: a journal folder under someone's profile, a Windows account name in
/// an audio error, a journal line that reached a log message.
/// </summary>
public class DiagnosticsRedactorTests
{
    private readonly DiagnosticsRedactor _redactor = new();

    [Fact]
    public void Redact_ReplacesAWindowsHomeDirectoryWithAPlaceholder()
    {
        var redacted = _redactor.Redact(
            @"The journal folder does not exist yet: C:\Users\Jameson\Saved Games\Frontier Developments\Elite Dangerous");

        Assert.Equal(
            @"The journal folder does not exist yet: [UserProfile]\Saved Games\Frontier Developments\Elite Dangerous",
            redacted);
        Assert.DoesNotContain("Jameson", redacted!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/home/hadesdtp/.local/share/EDButtkicker/user-settings.json", "hadesdtp")]
    [InlineData("/Users/hadesdtp/Library/EDButtkicker", "hadesdtp")]
    [InlineData(@"D:\Users\hadesdtp\Saved Games", "hadesdtp")]
    public void Redact_ReplacesHomeDirectoriesOnEveryPlatformThisAppRunsOn(string path, string accountName)
    {
        var redacted = _redactor.Redact(path)!;

        Assert.StartsWith("[UserProfile]", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(accountName, redacted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The paths this process resolves itself are the ones that actually show up in its logs and
    /// health reasons, and the most specific root has to win - reporting the settings folder as
    /// "[UserProfile]/.config/..." would hide which folder the app is really using.
    /// </summary>
    [Fact]
    public void Redact_ReplacesThisMachinesOwnRootsWithTheMostSpecificPlaceholder()
    {
        var settingsFile = Path.Combine(UserSettingsService.DefaultSettingsDirectory, "user-settings.json");

        var redacted = _redactor.Redact($"Loaded user preferences from {settingsFile}")!;

        Assert.StartsWith("Loaded user preferences from [", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            redacted,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redact_ReplacesTheAccountAndMachineName()
    {
        var redacted = _redactor.Redact(
            $"Device '{Environment.UserName}-buttkicker' on {Environment.MachineName} could not be opened")!;

        // Which placeholder lands where depends on whether the account or the host name is the longer
        // string; that neither of them survives is the part that matters.
        Assert.DoesNotContain(Environment.UserName, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-buttkicker' on [", redacted, StringComparison.Ordinal);
    }

    /// <summary>
    /// The worst thing a bundle could publish is a journal line, because one line carries the
    /// commander's name, their location and their balances at once. A log message that embedded one
    /// loses the whole payload rather than being picked over field by field.
    /// </summary>
    [Fact]
    public void Redact_RemovesJournalPayloadsFromFreeText()
    {
        const string journalLine =
            """{ "timestamp":"2026-09-13T10:14:26Z", "event":"Commander", "Name":"Hadesdtp", "Credits":982734511, "ShipID":17, "StarSystem":"Shinrarta Dezhra" }""";

        var redacted = _redactor.Redact($"Failed to parse journal line: {journalLine}")!;

        Assert.Equal("Failed to parse journal line: [journal payload removed]", redacted);
        Assert.DoesNotContain("Hadesdtp", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("982734511", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("Shinrarta", redacted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Structured log placeholders are braces without a JSON key, and have to survive.</summary>
    [Fact]
    public void Redact_LeavesOrdinaryBracesAlone()
    {
        const string message = "Watching {0} for *.json (subdirectories: True)";

        Assert.Equal(message, _redactor.Redact(message));
    }

    [Fact]
    public void Redact_KeepsAbsentAndEmptyValuesDistinguishable()
    {
        Assert.Null(_redactor.Redact(null));
        Assert.Equal(string.Empty, _redactor.Redact(string.Empty));
        Assert.Equal("[not set]", _redactor.RedactPath(null));
        Assert.Equal("[not set]", _redactor.RedactPath("   "));
    }

    [Fact]
    public void Redact_CapsTextSoTheUserCanReadWhatTheyArePublishing()
    {
        var redacted = _redactor.Redact(new string('x', DiagnosticsRedactor.MaxTextLength + 500))!;

        Assert.True(
            redacted.Length < DiagnosticsRedactor.MaxTextLength + 50,
            $"a {redacted.Length} character string is not a capped one");
        Assert.Contains("truncated", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_ScrubsValuesTheCallerKnowsAreIdentifying()
    {
        var redactor = new DiagnosticsRedactor(new[] { "Hadesdtp" });

        Assert.Equal("Commander [user] docked", redactor.Redact("Commander Hadesdtp docked"));
    }

    [Theory]
    [InlineData("apiToken")]
    [InlineData("API_KEY")]
    [InlineData("Password")]
    [InlineData("clientSecret")]
    [InlineData("refresh-token")]
    [InlineData("authorization")]
    public void IsSensitiveKey_RecognisesCredentialsWhateverTheyAreCalled(string key)
    {
        Assert.True(DiagnosticsRedactor.IsSensitiveKey(key));
    }

    [Theory]
    [InlineData("sampleRate")]
    [InlineData("journalPath")]
    [InlineData("audioDeviceName")]
    [InlineData("monitorLatestOnly")]
    [InlineData(null)]
    public void IsSensitiveKey_LeavesOrdinarySettingsAlone(string? key)
    {
        Assert.False(DiagnosticsRedactor.IsSensitiveKey(key));
    }
}
