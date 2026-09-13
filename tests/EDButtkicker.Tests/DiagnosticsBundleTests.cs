using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The support bundle is the one artifact a maintainer may ask a stranger to attach to a public
/// issue, so these tests are about two things at once: that it carries enough to triage a hardware
/// report (version, config, endpoints, audio state, watcher state, recent errors), and that it
/// carries nothing out of the reporter's save game or filesystem.
///
/// The bundle is built from the production service graph - the same registrations Program uses, with
/// per-user state in a temp directory and no real audio hardware.
/// </summary>
public class DiagnosticsBundleTests : IDisposable
{
    private readonly TempDirectory _settingsDir = new("edbk-diagnostics");
    private readonly TempDirectory _outputDir = new("edbk-diagnostics-out");

    private SetupTestHost NewHost(AppSettings? settings = null, FakeAudioDeviceCatalog? catalog = null)
    {
        var appSettings = settings ?? new AppSettings();

        return new SetupTestHost(
            _settingsDir.Path,
            appSettings,
            deviceCatalog: catalog ?? FakeAudioDeviceCatalog.With("ButtKicker Gamer Plus", "Headphones"),
            audioEngine: new FakeAudioEngine(appSettings));
    }

    private static DiagnosticsBundleService BundleServiceOf(SetupTestHost host) =>
        host.Services.GetRequiredService<DiagnosticsBundleService>();

    // ---- What the bundle has to contain ---------------------------------------------------------

    [Fact]
    public void Bundle_ReportsTheBuildConfigurationEndpointsAndBackendState()
    {
        var settings = new AppSettings();
        settings.Audio.AudioDeviceName = "ButtKicker Gamer Plus";
        settings.Audio.AudioDeviceEndpointId = FakeAudioDeviceCatalog.EndpointIdFor(0);
        settings.Audio.MaxIntensity = 73;

        using var host = NewHost(settings);
        host.AudioEngine.EnsureInitialized();

        var bundle = BundleServiceOf(host).Build();

        Assert.Equal(DiagnosticsBundleService.SchemaVersion, bundle.SchemaVersion);
        Assert.Equal("EDButtkicker", bundle.Application.Name);
        Assert.Equal(BuildVersion.Current, bundle.Application.Version);
        Assert.False(string.IsNullOrWhiteSpace(bundle.Application.Runtime));
        Assert.False(string.IsNullOrWhiteSpace(bundle.Application.OperatingSystem));

        Assert.Equal(73, bundle.Configuration.MaxIntensity);
        Assert.Equal(44100, bundle.Configuration.SampleRate);
        Assert.Equal("ButtKicker Gamer Plus", bundle.Configuration.AudioDeviceName);
        Assert.Equal(FakeAudioDeviceCatalog.EndpointIdFor(0), bundle.Configuration.AudioDeviceEndpointId);

        // The endpoint list is what a hardware report is actually about: ids and names, as the app
        // sees them, so a maintainer can tell "saved device is gone" from "device is not default".
        var endpoint = Assert.Single(bundle.AudioEndpoints, e => e.Name == "ButtKicker Gamer Plus");
        Assert.Equal(FakeAudioDeviceCatalog.EndpointIdFor(0), endpoint.EndpointId);
        Assert.Equal("WASAPI", endpoint.Driver);
        Assert.Contains(bundle.AudioEndpoints, e => e.Name == "Headphones");

        Assert.True(bundle.AudioBackend.Initialized);
        Assert.False(bundle.AudioBackend.InitializationFailed);
        Assert.Equal("FakeOut", bundle.AudioBackend.Backend);

        // The dashboard's own reading travels with the bundle rather than being re-derived by hand.
        Assert.Contains(bundle.Health, indicator => indicator.Id == "journal");
        Assert.Contains(bundle.Health, indicator => indicator.Id == "audio");
        Assert.All(bundle.Health, indicator => Assert.False(string.IsNullOrWhiteSpace(indicator.Reason)));
    }

    [Fact]
    public void Bundle_ReportsWhyTheAudioDeviceCouldNotBeOpened()
    {
        var settings = new AppSettings();
        using var host = new SetupTestHost(
            _settingsDir.Path,
            settings,
            audioEngine: new FakeAudioEngine(settings, canOpen: false));

        host.AudioEngine.EnsureInitialized();

        var backend = BundleServiceOf(host).Build().AudioBackend;

        Assert.False(backend.Initialized);
        Assert.True(backend.InitializationFailed);
        Assert.Contains("no output device is available", backend.LastError!, StringComparison.Ordinal);
    }

    [Fact]
    public void Bundle_ReportsTheWatcherStateRatherThanTheConfiguredFolder()
    {
        using var host = NewHost();

        var watcher = BundleServiceOf(host).Build().JournalWatcher;
        Assert.Equal(nameof(JournalWatchState.NotStarted), watcher.State);
        Assert.Null(watcher.ActiveFileName);

        host.Services.GetRequiredService<JournalMonitorStatus>()
            .ReportWatching(
                @"C:\Users\Hadesdtp\Saved Games\Frontier Developments\Elite Dangerous",
                "Journal.2026-09-13T101426.01.log",
                offset: 8192);

        watcher = BundleServiceOf(host).Build().JournalWatcher;

        Assert.Equal(nameof(JournalWatchState.Watching), watcher.State);
        Assert.Equal("Journal.2026-09-13T101426.01.log", watcher.ActiveFileName);
        Assert.Equal(8192, watcher.Offset);
        Assert.Equal(
            @"[UserProfile]\Saved Games\Frontier Developments\Elite Dangerous",
            watcher.Path);
        Assert.DoesNotContain("Hadesdtp", watcher.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bundle_ReportsRecentErrorsThroughTheLoggingPipeline()
    {
        using var host = NewHost();
        var errorLog = host.Services.GetRequiredService<RecentErrorLog>();

        // What the console provider would have shown, via the same ILogger the app uses.
        var logger = new RecentErrorLogProvider(errorLog).CreateLogger("EDButtkicker.Services.AudioEngineService");
        logger.LogInformation("Audio Engine initialized");
        logger.LogError(new InvalidOperationException("boom"), "Failed to initialize audio engine: {Reason}", "no device");

        var errors = BundleServiceOf(host).Build().RecentErrors;

        var error = Assert.Single(errors);
        Assert.Equal("Error", error.Level);
        Assert.Equal("EDButtkicker.Services.AudioEngineService", error.Category);
        Assert.Equal("Failed to initialize audio engine: no device", error.Message);
        Assert.Equal(typeof(InvalidOperationException).FullName, error.ExceptionType);
    }

    [Fact]
    public void Bundle_CarriesTheListOfWhatItLeavesOut()
    {
        using var host = NewHost();

        var bundle = BundleServiceOf(host).Build();

        Assert.NotEmpty(bundle.ExcludedByDesign);
        Assert.Contains(bundle.ExcludedByDesign, line => line.Contains("journal", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(bundle.ExcludedByDesign, line => line.Contains("Commander", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(bundle.ExcludedByDesign, line => line.Contains("path", StringComparison.OrdinalIgnoreCase));
    }

    // ---- What the bundle must never contain -----------------------------------------------------

    /// <summary>
    /// The journal event history is the one place in the process where real gameplay data lives. The
    /// bundle reports event names and counts - what <c>CONTRIBUTING.md</c> already asks reporters for -
    /// and nothing else out of an event.
    /// </summary>
    [Fact]
    public void Bundle_NeverLeaksAnythingFromAJournalEvent()
    {
        using var host = NewHost();
        var store = host.Services.GetRequiredService<IJournalEventStore>();

        store.Add(new JournalEvent
        {
            Timestamp = new DateTime(2026, 9, 13, 10, 14, 26, DateTimeKind.Utc),
            Event = "FSDJump",
            StarSystem = "Shinrarta Dezhra",
            SystemAddress = 3932277478106,
            StarPos = new[] { 55.71875, 17.59375, 27.15625 },
            Ship = "Anaconda",
            ShipID = 17,
            StationName = "Jameson Memorial",
            AdditionalData = new Dictionary<string, object>
            {
                ["Commander"] = "Hadesdtp",
                ["Credits"] = 982734511L,
                ["FuelLevel"] = 31.5
            }
        });
        store.Add(new JournalEvent { Timestamp = DateTime.UtcNow, Event = "FSDJump" });
        store.Add(new JournalEvent { Timestamp = DateTime.UtcNow, Event = "HullDamage" });

        var service = BundleServiceOf(host);
        var bundle = service.Build();
        var json = service.Render(bundle);

        // Event names and counts are in, and that is the whole of it.
        Assert.Equal(3, bundle.JournalWatcher.EventsSeenThisSession);
        Assert.Equal(2, bundle.JournalWatcher.EventTypeCounts["FSDJump"]);
        Assert.Equal(1, bundle.JournalWatcher.EventTypeCounts["HullDamage"]);

        foreach (var leak in new[]
                 {
                     "Shinrarta Dezhra", "Jameson Memorial", "Anaconda", "Hadesdtp",
                     "982734511", "3932277478106", "55.71875"
                 })
        {
            Assert.DoesNotContain(leak, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Bundle_ContainsNoAbsolutePathsFromThisMachine()
    {
        var settings = new AppSettings();
        settings.EliteDangerous.JournalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Saved Games", "Frontier Developments", "Elite Dangerous");

        using var host = NewHost(settings);
        host.Services.GetRequiredService<JournalMonitorStatus>()
            .ReportFaulted($"Journal monitoring stopped after an error: access to {settings.EliteDangerous.JournalPath} was denied");

        var service = BundleServiceOf(host);
        var json = service.Render(service.Build());

        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            json,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_settingsDir.Path, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[UserProfile]", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing in the settings file is a credential today. This is what keeps that true: a key this
    /// build does not bind is reported by name only, and a name that reads like a credential says so.
    /// </summary>
    [Fact]
    public void Bundle_NamesUnknownSettingsKeysAndNeverTheirValues()
    {
        File.WriteAllText(
            Path.Combine(_settingsDir.Path, "user-settings.json"),
            """
            {
              "audioDeviceName": "ButtKicker Gamer Plus",
              "maxIntensity": 80,
              "version": "1.0.0",
              "lastSaved": "2026-09-13T10:14:26Z",
              "apiToken": "sk-live-DO-NOT-LEAK-ME",
              "telemetryEndpoint": "https://example.invalid/collect"
            }
            """);

        using var host = NewHost();
        var service = BundleServiceOf(host);
        var bundle = service.Build();
        var json = service.Render(bundle);

        Assert.True(bundle.Configuration.UserSettings.Exists);
        Assert.Equal("1.0.0", bundle.Configuration.UserSettings.Version);
        Assert.Equal(
            new DateTime(2026, 9, 13, 10, 14, 26, DateTimeKind.Utc),
            bundle.Configuration.UserSettings.LastSavedUtc);

        Assert.Contains(
            bundle.Configuration.UserSettings.UnknownKeysWithheld,
            key => key.StartsWith("apiToken", StringComparison.Ordinal)
                && key.Contains("credential", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("telemetryEndpoint", bundle.Configuration.UserSettings.UnknownKeysWithheld);

        Assert.DoesNotContain("sk-live-DO-NOT-LEAK-ME", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.invalid", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bundle_SanitizesTheErrorsItReports()
    {
        using var host = NewHost();
        var errorLog = host.Services.GetRequiredService<RecentErrorLog>();
        var logger = new RecentErrorLogProvider(errorLog).CreateLogger("EDButtkicker.Services.JournalMonitorService");

        logger.LogError(
            "Error reading {Path}: {Line}",
            @"C:\Users\Hadesdtp\Saved Games\Frontier Developments\Elite Dangerous\Journal.log",
            """{ "event":"Commander", "Name":"Hadesdtp", "Credits":982734511 }""");

        var service = BundleServiceOf(host);
        var json = service.Render(service.Build());

        Assert.DoesNotContain("Hadesdtp", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("982734511", json, StringComparison.Ordinal);
        Assert.Contains("[journal payload removed]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Bundle_IsWellFormedJsonAUserCanRead()
    {
        using var host = NewHost();
        var service = BundleServiceOf(host);

        var json = service.Render(service.Build());

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            DiagnosticsBundleService.SchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("audioEndpoints", out _));
        Assert.True(document.RootElement.TryGetProperty("journalWatcher", out _));
        Assert.True(document.RootElement.TryGetProperty("recentErrors", out _));

        // Angle brackets would be escaped to \u003C and make the preview unreadable, which is why
        // the placeholders are bracketed instead.
        Assert.DoesNotContain("\\u003C", json, StringComparison.Ordinal);
    }

    // ---- Preview and confirmation ---------------------------------------------------------------

    [Fact]
    public async Task PreviewAndSave_WritesNothingUntilTheUserConfirms()
    {
        using var host = NewHost();
        var path = Path.Combine(_outputDir.Path, "bundle.json");
        var preview = new StringWriter();

        var result = await BundleServiceOf(host).PreviewAndSaveAsync(path, preview, new StringReader("no\n"));

        Assert.False(result.Saved);
        Assert.False(File.Exists(path), "a declined bundle must not leave a file behind");
        Assert.Contains("Nothing has been written yet", preview.ToString(), StringComparison.Ordinal);
        Assert.Contains("Cancelled", preview.ToString(), StringComparison.Ordinal);
    }

    /// <summary>An empty or closed stdin is not consent.</summary>
    [Fact]
    public async Task PreviewAndSave_TreatsNoAnswerAsADecline()
    {
        using var host = NewHost();
        var path = Path.Combine(_outputDir.Path, "bundle.json");

        var result = await BundleServiceOf(host)
            .PreviewAndSaveAsync(path, new StringWriter(), new StringReader(string.Empty));

        Assert.False(result.Saved);
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// The preview has to be the file, not a summary of it - that is what makes the confirmation
    /// informed consent rather than a prompt the user cannot answer.
    /// </summary>
    [Theory]
    [InlineData("yes")]
    [InlineData("Y")]
    public async Task PreviewAndSave_WritesExactlyWhatThePreviewShowed(string answer)
    {
        using var host = NewHost();
        var path = Path.Combine(_outputDir.Path, $"bundle-{answer}.json");
        var preview = new StringWriter();

        var result = await BundleServiceOf(host).PreviewAndSaveAsync(path, preview, new StringReader(answer));

        Assert.True(result.Saved, result.Reason);
        Assert.Equal(path, result.Path);
        Assert.True(File.Exists(path));

        var written = File.ReadAllText(path);
        Assert.Contains(written, preview.ToString(), StringComparison.Ordinal);
        Assert.Contains("Deliberately excluded", preview.ToString(), StringComparison.Ordinal);

        using var document = JsonDocument.Parse(written);
        Assert.Equal(
            DiagnosticsBundleService.SchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task PreviewAndSave_AcceptsAFolderAndNamesTheFileItself()
    {
        using var host = NewHost();

        var result = await BundleServiceOf(host)
            .PreviewAndSaveAsync(_outputDir.Path, new StringWriter(), new StringReader("yes"));

        Assert.True(result.Saved, result.Reason);
        Assert.Equal(_outputDir.Path, Path.GetDirectoryName(result.Path));
        Assert.StartsWith("diagnostics-bundle-", Path.GetFileName(result.Path), StringComparison.Ordinal);
        Assert.True(File.Exists(result.Path));
    }

    [Fact]
    public void SuggestedPath_SitsBesideTheOtherPerUserState()
    {
        using var host = NewHost();

        var suggested = BundleServiceOf(host).SuggestBundlePath();

        Assert.Equal(_settingsDir.Path, Path.GetDirectoryName(suggested));
        Assert.EndsWith(".json", suggested, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _settingsDir.Dispose();
        _outputDir.Dispose();
    }
}
