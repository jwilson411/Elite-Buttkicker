using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The public journal-path API used to change the settings object and nothing else, so the live
/// watcher stayed on the old folder until something else happened to request a re-check. These run
/// the real web pipeline next to a real monitor over temp folders - no game, no audio hardware -
/// and pin that POST /api/journal/path rebinds the watcher in place, and that the status endpoint
/// reports how far into the active file the reader has committed.
/// </summary>
public class JournalMonitorApiRebindTests : IDisposable
{
    private const string FsdJumpLine =
        """{"timestamp":"2026-08-28T12:00:00Z","event":"FSDJump","StarSystem":"Shinrarta Dezhra"}""";

    private readonly TempDirectory _root = new("edbk-monitor-api");
    private readonly RecordingJournalPipeline _pipeline = new();
    private readonly AppSettings _settings = new();

    private static string WriteJournal(string directory, string name, string line)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, line + "\n");
        return path;
    }

    /// <summary>
    /// The production graph on a TestServer, plus a monitor built from it with the recording
    /// pipeline in place of audio, so the controller and the watcher share one status object.
    /// </summary>
    private (SetupTestHost Host, JournalMonitorService Monitor) CreateHostAndMonitor()
    {
        var host = new SetupTestHost(_root.Path, _settings);
        var monitor = ActivatorUtilities.CreateInstance<JournalMonitorService>(host.Services, _pipeline);
        return (host, monitor);
    }

    [Fact]
    public async Task SetJournalPathApi_RebindsTheLiveWatcherWithoutRestartingTheHost()
    {
        var first = Path.Combine(_root.Path, "first");
        var second = Path.Combine(_root.Path, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        _settings.EliteDangerous.JournalPath = first;
        // Read the whole file rather than tailing, so the test does not race the writer.
        _settings.EliteDangerous.MonitorLatestOnly = false;

        var (host, monitor) = CreateHostAndMonitor();
        var status = host.Services.GetRequiredService<JournalMonitorStatus>();

        await monitor.StartAsync(CancellationToken.None);

        try
        {
            await SetupTestExtensions.WaitForAsync(
                () => status.Current.State == JournalWatchState.Watching && status.Current.Path == first,
                "the monitor to attach to the first folder");

            var journal = WriteJournal(second, "Journal.2026-08-28T130000.01.log", FsdJumpLine);

            var response = await host.PostAsync("/api/journal/path", new { path = second });
            Assert.True(response.IsSuccessStatusCode, $"POST /api/journal/path returned {(int)response.StatusCode}");

            await SetupTestExtensions.WaitForAsync(
                () => status.Current.Path == second && _pipeline.Processed.Count > 0,
                "the monitor to rebind to the folder the API was pointed at");

            Assert.Equal(JournalWatchState.Watching, status.Current.State);
            Assert.Equal(Path.GetFileName(journal), status.Current.ActiveFile);
            Assert.Equal("FSDJump", _pipeline.Processed[0].Event);

            var reported = await host.GetJsonAsync("/api/journal/status");
            Assert.True(reported.GetProperty("monitoring").GetBoolean());
            Assert.Equal(second, reported.GetProperty("journal_path").GetString());
            Assert.Equal(Path.GetFileName(journal), reported.GetProperty("monitor_active_file").GetString());
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            monitor.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public async Task StatusReportsTheReaderOffsetOnceALineHasBeenRead()
    {
        var journalPath = Path.Combine(_root.Path, "journals");
        Directory.CreateDirectory(journalPath);

        _settings.EliteDangerous.JournalPath = journalPath;
        _settings.EliteDangerous.MonitorLatestOnly = false;

        var (host, monitor) = CreateHostAndMonitor();
        var status = host.Services.GetRequiredService<JournalMonitorStatus>();

        var journal = WriteJournal(journalPath, "Journal.2026-08-28T120000.01.log", FsdJumpLine);
        var length = new FileInfo(journal).Length;

        await monitor.StartAsync(CancellationToken.None);

        try
        {
            await SetupTestExtensions.WaitForAsync(
                () => status.Current.Offset > 0 && _pipeline.Processed.Count > 0,
                "the monitor to report the offset it has committed in the journal file");

            // The cursor sits just past the newline that ended the only complete line.
            Assert.Equal(length, status.Current.Offset);

            var reported = await host.GetJsonAsync("/api/journal/status");
            Assert.Equal(status.Current.Offset, reported.GetProperty("monitor_offset").GetInt64());

            var events = await host.GetJsonAsync("/api/journal/events/recent");
            Assert.True(
                events.GetProperty("metadata").GetProperty("monitoring").GetBoolean(),
                "recent-event metadata should report the watcher state, not folder existence");
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            monitor.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public async Task RecentEventsMetadata_IsNotMonitoringWhileTheFolderIsMerelyPresent()
    {
        var journalPath = Path.Combine(_root.Path, "journals");
        Directory.CreateDirectory(journalPath);
        _settings.EliteDangerous.JournalPath = journalPath;

        // No monitor is running, so the folder existing must not read as monitoring.
        using var host = new SetupTestHost(_root.Path, _settings);

        var events = await host.GetJsonAsync("/api/journal/events/recent");

        Assert.False(events.GetProperty("metadata").GetProperty("monitoring").GetBoolean());
    }

    public void Dispose() => _root.Dispose();
}
