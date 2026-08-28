using System.Net;
using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Controllers;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The first run has to be guided and honest: discovery has to show what is really on disk, a step
/// only counts once the subsystem accepted it, a half-finished setup has to resume where it stopped,
/// and completion has to survive a restart without locking the wizard away. Everything runs against
/// temp directories and a fake device catalog - no audio hardware, no real user profile.
/// </summary>
public class SetupWizardApiTests : IDisposable
{
    private const string JournalLine =
        """{"timestamp":"2026-08-28T12:00:00Z","event":"FSDJump","StarSystem":"Shinrarta Dezhra"}""";

    private readonly TempDirectory _settingsDir = new("edbk-setup-state");
    private readonly TempDirectory _journalDir = new("edbk-setup-journal");

    /// <summary>A fresh host over the same settings directory - i.e. a restart of the app.</summary>
    private SetupTestHost NewHost(FakeAudioEngine? audioEngine = null, FakeAudioDeviceCatalog? catalog = null)
    {
        var settings = new AppSettings();
        // Point the configured path somewhere that does not exist, so nothing in these tests
        // depends on the developer's own Elite Dangerous install.
        settings.EliteDangerous.JournalPath = Path.Combine(_settingsDir.Path, "not-installed");

        return new SetupTestHost(
            _settingsDir.Path,
            settings,
            catalog,
            audioEngine ?? new FakeAudioEngine(settings),
            journalSearchPaths: new[] { _journalDir.Path });
    }

    private string WriteJournalFile(string name = "Journal.2026-08-28T120000.01.log")
    {
        var path = Path.Combine(_journalDir.Path, name);
        File.WriteAllText(path, JournalLine + Environment.NewLine);
        return path;
    }

    [Fact]
    public async Task FirstRun_OpensTheWizardWithNothingConfirmed()
    {
        using var host = NewHost();

        var status = await host.GetJsonAsync("/api/setup/status");

        Assert.False(status.GetProperty("completed").GetBoolean());
        Assert.True(status.GetProperty("show_wizard").GetBoolean());
        Assert.Equal("journal", status.GetProperty("current_step").GetString());
        Assert.All(
            status.GetProperty("steps").EnumerateArray(),
            step => Assert.False(step.GetProperty("complete").GetBoolean()));

        // The wizard carries real subsystem health from the very first screen.
        Assert.NotEmpty(status.GetProperty("health").GetProperty("components").EnumerateArray());
    }

    [Fact]
    public async Task JournalDiscovery_ReportsWhatIsActuallyInEachFolder()
    {
        WriteJournalFile();
        using var host = NewHost();

        var discovery = await host.GetJsonAsync("/api/setup/journal/candidates");
        var candidates = discovery.GetProperty("candidates").EnumerateArray().ToList();

        var configured = candidates.Single(c => c.GetProperty("is_configured").GetBoolean());
        Assert.False(configured.GetProperty("exists").GetBoolean());

        var found = candidates.Single(c => c.GetProperty("path").GetString() == _journalDir.Path);
        Assert.True(found.GetProperty("exists").GetBoolean());
        Assert.Equal(1, found.GetProperty("journal_files_found").GetInt32());

        // The folder that actually holds journals is the one recommended.
        Assert.Equal(_journalDir.Path, discovery.GetProperty("recommended_path").GetString());
    }

    [Fact]
    public async Task JournalStep_ConfirmingAFolderPersistsItAndCompletesTheStep()
    {
        WriteJournalFile();
        using var host = NewHost();

        var response = await host.PostAsync("/api/setup/journal", new { path = _journalDir.Path });
        var result = await SetupTestHost.ReadJsonAsync(response);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(1, result.GetProperty("journal_files_found").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("warning").ValueKind);

        Assert.Equal(_journalDir.Path, host.Settings.EliteDangerous.JournalPath);
        Assert.True(File.Exists(Path.Combine(_settingsDir.Path, "user-settings.json")));
        Assert.True(File.Exists(Path.Combine(_settingsDir.Path, "setup-state.json")));

        var setup = result.GetProperty("setup");
        Assert.True(setup.IsStepComplete("journal"));
        Assert.False(setup.IsStepComplete("audio-device"));
        Assert.Equal("audio-device", setup.GetProperty("current_step").GetString());
    }

    [Fact]
    public async Task JournalStep_RejectsAFolderThatDoesNotExist()
    {
        using var host = NewHost();

        var response = await host.PostAsync(
            "/api/setup/journal",
            new { path = Path.Combine(_settingsDir.Path, "nowhere") });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var status = await host.GetJsonAsync("/api/setup/status");
        Assert.False(status.IsStepComplete("journal"));
    }

    [Fact]
    public async Task JournalStep_AcceptsAFolderWithNoJournalsYetButSaysSo()
    {
        using var host = NewHost();

        var response = await host.PostAsync("/api/setup/journal", new { path = _journalDir.Path });
        var result = await SetupTestHost.ReadJsonAsync(response);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(0, result.GetProperty("journal_files_found").GetInt32());
        Assert.Contains("No journal files here yet", result.GetProperty("warning").GetString());
        Assert.True(result.GetProperty("setup").IsStepComplete("journal"));
    }

    [Fact]
    public async Task AudioStep_StoresTheDeviceNameSoTheChoiceSurvivesReordering()
    {
        using var host = NewHost();

        var response = await host.PostAsync("/api/setup/audio/device", new { deviceId = 0 });
        var result = await SetupTestHost.ReadJsonAsync(response);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("ButtKicker Amp", result.GetProperty("device").GetProperty("name").GetString());

        // The name is the stable key: the enumeration index moves when devices come and go.
        Assert.Equal("ButtKicker Amp", host.Settings.Audio.AudioDeviceName);
        Assert.Equal(0, host.Settings.Audio.AudioDeviceId);

        var persisted = await File.ReadAllTextAsync(Path.Combine(_settingsDir.Path, "setup-state.json"));
        Assert.Contains("ButtKicker Amp", persisted);
        Assert.True(result.GetProperty("setup").IsStepComplete("audio-device"));
    }

    [Fact]
    public async Task AudioStep_SystemDefaultIsStoredAsTheDefaultRatherThanAName()
    {
        using var host = NewHost();

        var response = await host.PostAsync("/api/setup/audio/device", new { deviceId = -1 });

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(string.Empty, host.Settings.Audio.AudioDeviceName);
        Assert.Equal(-1, host.Settings.Audio.AudioDeviceId);
    }

    [Fact]
    public async Task AudioStep_RejectsADeviceThatIsNotConnected()
    {
        using var host = NewHost();

        var response = await host.PostAsync("/api/setup/audio/device", new { name = "Not Plugged In" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(string.Empty, host.Settings.Audio.AudioDeviceName);
    }

    [Fact]
    public async Task AudioTest_PlaysAConservativePattern()
    {
        var settings = new AppSettings();
        using var host = NewHost(new FakeAudioEngine(settings));

        var response = await host.PostAsync("/api/setup/audio/test");
        var result = await SetupTestHost.ReadJsonAsync(response);

        Assert.True(response.IsSuccessStatusCode);
        Assert.True(result.GetProperty("played").GetBoolean());

        var played = Assert.Single(host.AudioEngine.Played);
        Assert.True(played.Intensity <= SetupApiController.TestIntensityPercent, "the test tone must stay quiet");
        Assert.Equal(SetupApiController.TestDurationMs, played.Duration);
        Assert.InRange(played.Frequency, SetupApiController.MinTestFrequency, SetupApiController.MaxTestFrequency);
        Assert.True(played.FadeIn > 0 && played.FadeOut > 0, "the test tone must fade rather than start at full level");
    }

    [Fact]
    public async Task AudioTest_SaysNothingPlayedWhenNoDeviceCanBeOpened()
    {
        var settings = new AppSettings();
        using var host = NewHost(new FakeAudioEngine(settings, canOpen: false));

        var response = await host.PostAsync("/api/setup/audio/test");
        var result = await SetupTestHost.ReadJsonAsync(response);

        Assert.True(response.IsSuccessStatusCode);
        Assert.False(result.GetProperty("played").GetBoolean());
        Assert.Contains("no output device is available", result.GetProperty("reason").GetString());
        Assert.Empty(host.AudioEngine.Played);

        // The step is recorded as run, with the outcome that actually happened.
        var step = result.GetProperty("setup").Step("audio-test");
        Assert.True(step.GetProperty("complete").GetBoolean());
        Assert.Contains("nothing was played", step.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task PartialConfiguration_ResumesAtTheNextStepAfterARestart()
    {
        WriteJournalFile();

        using (var first = NewHost())
        {
            var response = await first.PostAsync("/api/setup/journal", new { path = _journalDir.Path });
            Assert.True(response.IsSuccessStatusCode);
        }

        using var restarted = NewHost();
        var status = await restarted.GetJsonAsync("/api/setup/status");

        Assert.False(status.GetProperty("completed").GetBoolean());
        Assert.True(status.GetProperty("show_wizard").GetBoolean());
        Assert.True(status.IsStepComplete("journal"));
        Assert.False(status.IsStepComplete("audio-device"));
        Assert.Equal("audio-device", status.GetProperty("current_step").GetString());
        Assert.Equal(
            new[] { "audio-device", "audio-test", "finish" },
            status.GetProperty("incomplete_steps").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task ChangingTheOutputDevice_InvalidatesThePreviousAudioTest()
    {
        using var host = NewHost();

        Assert.True((await host.PostAsync("/api/setup/audio/test")).IsSuccessStatusCode);
        Assert.True((await host.GetJsonAsync("/api/setup/status")).IsStepComplete("audio-test"));

        var response = await host.PostAsync("/api/setup/audio/device", new { deviceId = 1 });
        var result = await SetupTestHost.ReadJsonAsync(response);

        // A test on the previous device says nothing about the new one.
        Assert.False(result.GetProperty("setup").IsStepComplete("audio-test"));
    }

    [Fact]
    public async Task Completion_IsPersistedAndStopsTheWizardOpeningItself()
    {
        using (var first = NewHost())
        {
            var response = await first.PostAsync("/api/setup/complete");
            var result = await SetupTestHost.ReadJsonAsync(response);

            Assert.True(response.IsSuccessStatusCode);
            Assert.True(result.GetProperty("setup").GetProperty("completed").GetBoolean());
            Assert.False(result.GetProperty("setup").GetProperty("show_wizard").GetBoolean());
        }

        using var restarted = NewHost();
        var status = await restarted.GetJsonAsync("/api/setup/status");

        Assert.True(status.GetProperty("completed").GetBoolean());
        Assert.False(status.GetProperty("show_wizard").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("completed_at").ValueKind);
    }

    [Fact]
    public async Task Reopening_ShowsTheWizardAgainWithoutErasingCompletion()
    {
        using var host = NewHost();
        Assert.True((await host.PostAsync("/api/setup/complete")).IsSuccessStatusCode);

        var reopened = (await SetupTestHost.ReadJsonAsync(await host.PostAsync("/api/setup/reopen")))
            .GetProperty("setup");

        Assert.True(reopened.GetProperty("show_wizard").GetBoolean());
        Assert.True(reopened.GetProperty("reopen_requested").GetBoolean());
        // Completion is a record, not something a reopen throws away.
        Assert.True(reopened.GetProperty("completed").GetBoolean());

        var finished = (await SetupTestHost.ReadJsonAsync(await host.PostAsync("/api/setup/complete")))
            .GetProperty("setup");

        Assert.False(finished.GetProperty("show_wizard").GetBoolean());
        Assert.True(finished.GetProperty("completed").GetBoolean());
    }

    [Fact]
    public async Task Reopening_DoesNotSurviveARestart()
    {
        using (var first = NewHost())
        {
            Assert.True((await first.PostAsync("/api/setup/complete")).IsSuccessStatusCode);
            Assert.True((await first.PostAsync("/api/setup/reopen")).IsSuccessStatusCode);
        }

        using var restarted = NewHost();
        var status = await restarted.GetJsonAsync("/api/setup/status");

        Assert.False(status.GetProperty("show_wizard").GetBoolean());
        Assert.False(status.GetProperty("reopen_requested").GetBoolean());
    }

    [Fact]
    public async Task DamagedSetupState_FallsBackToAFirstRunInsteadOfFailing()
    {
        await File.WriteAllTextAsync(Path.Combine(_settingsDir.Path, "setup-state.json"), "{ not json");

        using var host = NewHost();
        var status = await host.GetJsonAsync("/api/setup/status");

        Assert.False(status.GetProperty("completed").GetBoolean());
        Assert.True(status.GetProperty("show_wizard").GetBoolean());
    }

    public void Dispose()
    {
        _settingsDir.Dispose();
        _journalDir.Dispose();
    }
}
