using System.Net;
using EDButtkicker.Configuration;
using EDButtkicker.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Every indicator has to come from the subsystem it names. These pin the states the dashboard used
/// to fake: journal monitoring reported from an existing folder rather than a live watcher, audio
/// and voice reported "online" because an unrelated request succeeded. Each indicator also has to
/// carry a reason, and a retry that does something.
/// </summary>
public class SystemHealthApiTests : IDisposable
{
    private readonly TempDirectory _settingsDir = new("edbk-health");

    private SetupTestHost NewHost(AppSettings? settings = null, FakeAudioEngine? audioEngine = null)
    {
        var appSettings = settings ?? new AppSettings();
        return new SetupTestHost(_settingsDir.Path, appSettings, audioEngine: audioEngine ?? new FakeAudioEngine(appSettings));
    }

    [Fact]
    public async Task Journal_TracksTheWatcherRatherThanTheFolder()
    {
        using var host = NewHost();
        var monitor = host.Services.GetRequiredService<JournalMonitorStatus>();

        var report = await host.GetJsonAsync("/api/health");
        Assert.Equal("pending", report.StatusOf("journal"));
        Assert.Contains("has not started", report.ReasonOf("journal"));

        monitor.ReportWaiting("C:\\journals", "The journal folder does not exist yet: C:\\journals");
        report = await host.GetJsonAsync("/api/health");
        Assert.Equal("attention", report.StatusOf("journal"));
        Assert.Contains("does not exist yet", report.ReasonOf("journal"));

        // Attached but with no journal file yet is still not "connected".
        monitor.ReportWatching("C:\\journals", null);
        report = await host.GetJsonAsync("/api/health");
        Assert.Equal("attention", report.StatusOf("journal"));

        monitor.ReportWatching("C:\\journals", "Journal.2026-08-28T120000.01.log");
        report = await host.GetJsonAsync("/api/health");
        Assert.Equal("ok", report.StatusOf("journal"));
        Assert.Contains("Journal.2026-08-28T120000.01.log", report.ReasonOf("journal"));
    }

    [Fact]
    public async Task Journal_FaultIsReportedAsAnErrorWithItsReason()
    {
        using var host = NewHost();
        host.Services.GetRequiredService<JournalMonitorStatus>()
            .ReportFaulted("Journal monitoring stopped after an error: access denied");

        var report = await host.GetJsonAsync("/api/health");

        Assert.Equal("error", report.StatusOf("journal"));
        Assert.Contains("access denied", report.ReasonOf("journal"));
    }

    [Fact]
    public async Task JournalStatusApi_OnlyReportsMonitoringWhenAWatcherIsAttached()
    {
        using var dir = new TempDirectory("edbk-health-journal");
        var settings = new AppSettings();
        settings.EliteDangerous.JournalPath = dir.Path;

        using var host = NewHost(settings);
        var monitor = host.Services.GetRequiredService<JournalMonitorStatus>();

        // The folder exists, which used to be enough to claim "Connected".
        var status = await host.GetJsonAsync("/api/journal/status");
        Assert.True(status.GetProperty("path_exists").GetBoolean());
        Assert.False(status.GetProperty("monitoring").GetBoolean());
        Assert.Equal("Disconnected", status.GetProperty("status").GetString());

        monitor.ReportWatching(dir.Path, "Journal.2026-08-28T120000.01.log");
        status = await host.GetJsonAsync("/api/journal/status");

        Assert.True(status.GetProperty("monitoring").GetBoolean());
        Assert.Equal("Connected", status.GetProperty("status").GetString());
        Assert.Equal("Watching", status.GetProperty("monitor_state").GetString());
    }

    [Fact]
    public async Task Audio_IsPendingUntilADeviceIsActuallyOpened()
    {
        using var host = NewHost();

        var report = await host.GetJsonAsync("/api/health");
        Assert.Equal("pending", report.StatusOf("audio"));
        Assert.Contains("has been opened yet", report.ReasonOf("audio"));

        Assert.True((await host.PostAsync("/api/setup/audio/test")).IsSuccessStatusCode);

        report = await host.GetJsonAsync("/api/health");
        Assert.Equal("ok", report.StatusOf("audio"));
    }

    [Fact]
    public async Task Audio_ReportsWhyTheDeviceCouldNotBeOpened()
    {
        var settings = new AppSettings();
        using var host = NewHost(settings, new FakeAudioEngine(settings, canOpen: false));

        Assert.True((await host.PostAsync("/api/setup/audio/test")).IsSuccessStatusCode);

        var report = await host.GetJsonAsync("/api/health");
        var audio = report.Component("audio");

        Assert.Equal("error", audio.GetProperty("status").GetString());
        Assert.Contains("no output device is available", audio.GetProperty("reason").GetString());
        Assert.Equal("/api/health/audio/retry", audio.GetProperty("retry").GetProperty("endpoint").GetString());
    }

    [Fact]
    public async Task Audio_FlagsASavedDeviceThatIsNoLongerConnected()
    {
        var settings = new AppSettings();
        settings.Audio.AudioDeviceName = "ButtKicker Amp That Went Away";

        using var host = NewHost(settings);

        var report = await host.GetJsonAsync("/api/health");

        Assert.Equal("attention", report.StatusOf("audio"));
        Assert.Contains("is not connected", report.ReasonOf("audio"));
    }

    [Fact]
    public async Task Voice_IsReportedOffInsteadOfOnline()
    {
        using var host = NewHost();

        var report = await host.GetJsonAsync("/api/health");

        Assert.Equal("off", report.StatusOf("voice"));
        Assert.Contains("not running", report.ReasonOf("voice"));
    }

    [Fact]
    public async Task WebAndPatterns_ReportWhatTheProcessIsActuallyDoing()
    {
        using var host = NewHost();

        var report = await host.GetJsonAsync("/api/health");

        Assert.Equal("ok", report.StatusOf("web"));
        Assert.Contains("loopback", report.ReasonOf("web"));

        // Default event mappings are loaded by the running service, so this is a real reading.
        Assert.Equal("ok", report.StatusOf("patterns"));
        Assert.Contains("pattern", report.ReasonOf("patterns"));
    }

    [Fact]
    public async Task Retry_ForTheJournal_AsksTheMonitorToLookAgain()
    {
        using var host = NewHost();
        var monitor = host.Services.GetRequiredService<JournalMonitorStatus>();

        var response = await host.PostAsync("/api/health/journal/retry");
        var result = await SetupTestHost.ReadJsonAsync(response);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("journal", result.GetProperty("retried").GetString());
        Assert.NotEmpty(result.GetProperty("health").GetProperty("components").EnumerateArray());

        // The recheck signal is queued for the watcher, which is what makes the retry real.
        Assert.True(await monitor.WaitForRecheckAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task Retry_ForAudio_ReopensTheDeviceAndReportsTheNewState()
    {
        var settings = new AppSettings();
        var engine = new FakeAudioEngine(settings, failuresBeforeSuccess: 1);
        using var host = NewHost(settings, engine);

        // First attempt fails, exactly like a device that was not ready at startup.
        Assert.False((await SetupTestHost.ReadJsonAsync(await host.PostAsync("/api/setup/audio/test")))
            .GetProperty("played").GetBoolean());
        Assert.Equal("error", (await host.GetJsonAsync("/api/health")).StatusOf("audio"));

        var retry = await SetupTestHost.ReadJsonAsync(await host.PostAsync("/api/health/audio/retry"));

        Assert.Equal("ok", retry.GetProperty("component").GetProperty("status").GetString());
        Assert.Equal("ok", retry.GetProperty("health").StatusOf("audio"));
        Assert.Equal(2, engine.OpenAttempts);
    }

    [Fact]
    public async Task Retry_ForAComponentWithoutOne_IsRejected()
    {
        using var host = NewHost();

        var response = await host.PostAsync("/api/health/web/retry");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EveryIndicator_CarriesAReason()
    {
        using var host = NewHost();

        var report = await host.GetJsonAsync("/api/health");

        Assert.All(report.GetProperty("components").EnumerateArray(), component =>
        {
            Assert.False(string.IsNullOrWhiteSpace(component.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(component.GetProperty("reason").GetString()));
        });

        // Overall status is the worst of the parts, so a green header cannot hide a red row.
        Assert.Equal("pending", report.GetProperty("status").GetString());
    }

    public void Dispose() => _settingsDir.Dispose();
}
