using EDButtkicker.Configuration;
using EDButtkicker.Hosting;
using EDButtkicker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Recovery: a journal folder that is missing at startup used to end journal monitoring for the
/// lifetime of the process, and the dashboard showed the folder's existence instead of the watcher.
/// These drive the real service over temp folders - no game, no audio hardware - and pin that it
/// waits, says why, attaches when the folder appears, follows a path change from setup, and goes
/// back to waiting when the folder disappears.
/// </summary>
public class JournalMonitorRecoveryTests : IDisposable
{
    private const string FsdJumpLine =
        """{"timestamp":"2026-08-28T12:00:00Z","event":"FSDJump","StarSystem":"Shinrarta Dezhra"}""";

    private readonly TempDirectory _root = new("edbk-monitor");
    private readonly RecordingJournalPipeline _pipeline = new();
    private readonly AppSettings _settings = new();
    private ServiceProvider? _provider;

    private JournalMonitorService CreateMonitor()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddEliteButtkicker(_settings);

        // Journal events are recorded instead of played, and per-user state stays in the temp folder.
        services.Replace(ServiceDescriptor.Singleton<IJournalEventPipeline>(_pipeline));
        services.Replace(ServiceDescriptor.Singleton<AudioEngineService>(new FakeAudioEngine(_settings)));
        services.Replace(ServiceDescriptor.Singleton(
            new UserSettingsService(NullLogger<UserSettingsService>.Instance, _root.Path)));

        _provider = services.BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<JournalMonitorService>(_provider);
    }

    private JournalMonitorStatus Status => _provider!.GetRequiredService<JournalMonitorStatus>();

    private static string WriteJournal(string directory, string name, string line)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, line + "\n");
        return path;
    }

    [Fact]
    public async Task MissingFolder_IsWaitedOnAndAttachedToWhenItAppears()
    {
        var journalPath = Path.Combine(_root.Path, "journals");
        _settings.EliteDangerous.JournalPath = journalPath;
        // Read the whole file rather than tailing, so the test does not race the writer.
        _settings.EliteDangerous.MonitorLatestOnly = false;

        var monitor = CreateMonitor();
        await monitor.StartAsync(CancellationToken.None);

        try
        {
            await SetupTestExtensions.WaitForAsync(
                () => Status.Current.State == JournalWatchState.Waiting,
                "the monitor to report that it is waiting for the journal folder");

            // The reason names the folder, which is what the health indicator shows the user.
            Assert.Contains(journalPath, Status.Current.Reason);

            Directory.CreateDirectory(journalPath);
            var file = WriteJournal(journalPath, "Journal.2026-08-28T120000.01.log", FsdJumpLine);
            Status.RequestRecheck();

            await SetupTestExtensions.WaitForAsync(
                () => Status.Current.State == JournalWatchState.Watching && _pipeline.Processed.Count > 0,
                "the monitor to attach to the new folder and process the journal line");

            Assert.Equal("FSDJump", _pipeline.Processed[0].Event);
            Assert.Equal(Path.GetFileName(file), Status.Current.ActiveFile);
            Assert.Equal(journalPath, Status.Current.Path);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            monitor.Dispose();
        }
    }

    [Fact]
    public async Task ConfiguredPathChange_MovesTheWatcherToTheNewFolder()
    {
        var first = Path.Combine(_root.Path, "first");
        var second = Path.Combine(_root.Path, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        _settings.EliteDangerous.JournalPath = first;
        _settings.EliteDangerous.MonitorLatestOnly = false;

        var monitor = CreateMonitor();
        await monitor.StartAsync(CancellationToken.None);

        try
        {
            await SetupTestExtensions.WaitForAsync(
                () => Status.Current.State == JournalWatchState.Watching && Status.Current.Path == first,
                "the monitor to attach to the first folder");

            // This is what the setup wizard does when the user confirms a different folder.
            WriteJournal(second, "Journal.2026-08-28T130000.01.log", FsdJumpLine);
            _settings.EliteDangerous.JournalPath = second;
            Status.RequestRecheck();

            await SetupTestExtensions.WaitForAsync(
                () => Status.Current.Path == second && _pipeline.Processed.Count > 0,
                "the monitor to follow the journal path change");

            Assert.Equal(JournalWatchState.Watching, Status.Current.State);
            Assert.Equal("FSDJump", _pipeline.Processed[0].Event);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            monitor.Dispose();
        }
    }

    [Fact]
    public async Task FolderDisappearing_ReturnsTheMonitorToWaitingInsteadOfSilence()
    {
        var journalPath = Path.Combine(_root.Path, "journals");
        Directory.CreateDirectory(journalPath);
        _settings.EliteDangerous.JournalPath = journalPath;

        var monitor = CreateMonitor();
        await monitor.StartAsync(CancellationToken.None);

        try
        {
            await SetupTestExtensions.WaitForAsync(
                () => Status.Current.State == JournalWatchState.Watching,
                "the monitor to attach to the journal folder");

            Directory.Delete(journalPath, recursive: true);

            await SetupTestExtensions.WaitForAsync(
                () => Status.Current.State == JournalWatchState.Waiting,
                "the monitor to report that the folder is gone");

            Assert.Contains(journalPath, Status.Current.Reason);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            monitor.Dispose();
        }
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _root.Dispose();
    }
}
