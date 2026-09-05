using System.Text;
using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Controllers;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Replay used to combine the caller's journalFile onto the journal folder and read whatever came
/// out. These drive the controller directly - no port, no audio, no Elite Dangerous - so the
/// assertion can be the strong one: the file outside the journal folder is never replayed.
/// </summary>
public class JournalReplayPathTraversalTests
{
    private const string LegitJournalName = "Journal.2026-01-01T000000.01.log";
    private const string SecretEventName = "SecretLeakEvent";

    [Theory]
    [InlineData("../secret.log")]
    [InlineData("..\\secret.log")]
    [InlineData("..%2fsecret.log")]
    [InlineData("%2e%2e%2fsecret.log")]
    [InlineData("Journal.foo/../../secret.log")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("secret.log")]
    [InlineData("Journal.2026-09-09T000000.01.log")]
    public async Task StartJournalReplay_WithAFileOutsideTheEnumeration_Returns400AndReplaysNothing(string journalFile)
    {
        await using var fixture = new ReplayFixture();

        var response = await fixture.PostReplayStart(journalFile);

        Assert.Equal(400, response.StatusCode);
        Assert.DoesNotContain(SecretEventName, response.Body);
        await fixture.AssertNothingWasReplayed();
    }

    [Fact]
    public async Task StartJournalReplay_WithARootedPathToTheSecret_Returns400AndReplaysNothing()
    {
        await using var fixture = new ReplayFixture();

        var response = await fixture.PostReplayStart(fixture.SecretPath);

        Assert.Equal(400, response.StatusCode);
        await fixture.AssertNothingWasReplayed();
    }

    [Fact]
    public async Task StartJournalReplay_WithAnEnumeratedJournalFile_IsAccepted()
    {
        await using var fixture = new ReplayFixture();

        var response = await fixture.PostReplayStart(LegitJournalName);

        Assert.Equal(200, response.StatusCode);

        using var document = JsonDocument.Parse(response.Body);
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(LegitJournalName, document.RootElement.GetProperty("source").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("events_count").GetInt32());

        await fixture.StopReplay();
    }

    [Fact]
    public async Task StartJournalReplay_WithoutAJournalFile_StillFallsBackToRecentEvents()
    {
        await using var fixture = new ReplayFixture();
        fixture.EventStore.Add(new JournalEvent
        {
            Timestamp = DateTime.UtcNow,
            Event = "FSDJump",
            StarSystem = "In Memory"
        });

        var response = await fixture.PostReplayStart(journalFile: null);

        Assert.Equal(200, response.StatusCode);

        using var document = JsonDocument.Parse(response.Body);
        Assert.Equal("recent_events", document.RootElement.GetProperty("source").GetString());

        await fixture.StopReplay();
    }

    /// <summary>
    /// The restart path over the controller. Starting again used to block the request thread on the
    /// previous replay task while holding that replay's lock, so this asserts on the clock: both
    /// requests, and the stop, return well inside the old two second wait.
    /// </summary>
    [Fact]
    public async Task StartJournalReplay_WhileAReplayIsRunning_RestartsWithoutBlockingTheRequest()
    {
        await using var fixture = new ReplayFixture();

        var first = await fixture.PostReplayStart(LegitJournalName).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(200, first.StatusCode);

        // The journal's two events are 30 seconds apart, so the first replay is still sitting in
        // its capped gap when the second request arrives.
        var second = await fixture.PostReplayStart(LegitJournalName).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(200, second.StatusCode);

        await fixture.StopReplay().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(fixture.Replay.GetStatus().IsReplaying);
    }

    private sealed record ReplayResponse(int StatusCode, string Body);

    /// <summary>
    /// A temp journal folder holding one legitimate journal file, and a secret file in the parent
    /// folder that no request may reach. The controller is the real one, wired to fakes.
    /// </summary>
    private sealed class ReplayFixture : IAsyncDisposable
    {
        private readonly TempDirectory _root = new("edbk-journal-replay");

        public ReplayFixture()
        {
            JournalDirectory = Path.Combine(_root.Path, "journals");
            Directory.CreateDirectory(JournalDirectory);

            var now = DateTime.UtcNow;
            File.WriteAllLines(Path.Combine(JournalDirectory, LegitJournalName), new[]
            {
                JournalLine(now.AddSeconds(-30), "FSDJump", "Shinrarta Dezhra"),
                JournalLine(now, "HullDamage", "Shinrarta Dezhra")
            });

            // Outside the journal folder, but shaped so that it would replay cleanly if ever read -
            // so a leak shows up as a replayed SecretLeakEvent rather than as a parse failure.
            SecretPath = Path.Combine(_root.Path, "secret.log");
            File.WriteAllLines(SecretPath, new[] { JournalLine(now, SecretEventName, "Sol") });

            Settings = new AppSettings();
            Settings.EliteDangerous.JournalPath = JournalDirectory;

            var userSettings = new UserSettingsService(
                NullLogger<UserSettingsService>.Instance, _root.Path);

            var persistence = new SettingsPersistenceService(
                NullLogger<SettingsPersistenceService>.Instance,
                Settings,
                userSettings,
                new AudioEngineService(NullLogger<AudioEngineService>.Instance, Settings),
                new JournalMonitorStatus(TimeProvider.System));

            Replay = new JournalReplayService(NullLogger<JournalReplayService>.Instance, Pipeline);

            Controller = new JournalApiController(
                NullLogger<JournalApiController>.Instance,
                Settings,
                EventStore,
                new JournalMonitorStatus(TimeProvider.System),
                persistence,
                Replay);
        }

        public string JournalDirectory { get; }

        public string SecretPath { get; }

        public AppSettings Settings { get; }

        public JournalEventStore EventStore { get; } = new();

        public RecordingJournalPipeline Pipeline { get; } = new();

        public JournalReplayService Replay { get; }

        public JournalApiController Controller { get; }

        public async Task<ReplayResponse> PostReplayStart(string? journalFile)
        {
            var context = new DefaultHttpContext();
            var responseBody = new MemoryStream();
            context.Response.Body = responseBody;

            if (journalFile != null)
            {
                var bytes = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new Dictionary<string, string> { ["journalFile"] = journalFile }));
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = bytes.Length;
                context.Request.Body = new MemoryStream(bytes);
            }

            await Controller.StartJournalReplay(context);

            return new ReplayResponse(context.Response.StatusCode, Encoding.UTF8.GetString(responseBody.ToArray()));
        }

        public Task StopReplay()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();

            return Controller.StopJournalReplay(context);
        }

        /// <summary>
        /// Nothing reached the pipeline, including after the delay a started replay would have
        /// needed to hand over its first event.
        /// </summary>
        public async Task AssertNothingWasReplayed()
        {
            Assert.Empty(Pipeline.Processed);

            await Task.Delay(50);

            Assert.Empty(Pipeline.Processed);
        }

        /// <summary>
        /// Teardown drains the replay instead of only cancelling it, so no run is still handing
        /// events to the pipeline while the next test's temp folder is being built.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await Replay.DisposeAsync();
            _root.Dispose();
        }

        private static string JournalLine(DateTime timestamp, string eventName, string starSystem) =>
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["timestamp"] = timestamp.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["event"] = eventName,
                ["StarSystem"] = starSystem
            });
    }
}
