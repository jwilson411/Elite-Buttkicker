using EDButtkicker.Services;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The pattern catalog reloads because a watcher event arrived - not because the OS happened to
/// raise one. <see cref="PatternFileWatcherDebounceTests"/> runs the real
/// <see cref="FileSystemWatcher"/> against a temp directory, which is a fair end-to-end check but
/// silently proves nothing on a filesystem that does not deliver events. These drive
/// <see cref="IDirectoryWatcher"/> by hand, so the reload path is pinned either way.
/// </summary>
public class PatternWatcherSeamTests
{
    private const string ValidPatternJson = """
    {
      "metadata": { "name": "Seam Test", "version": "1.0.0", "author": "Tester", "description": "d", "tags": [], "created": "2026-01-01T00:00:00Z", "compatibility": "1.0.0" },
      "ships": { "sidewinder": { "displayName": "Sidewinder", "class": "small", "role": "combat", "events": {} } }
    }
    """;

    private static PatternWatchOptions FastOptions(int debounceMs = 150) => new()
    {
        DebounceWindow = TimeSpan.FromMilliseconds(debounceMs),
        StabilityWindow = TimeSpan.FromMilliseconds(50),
        MaxStabilityWait = TimeSpan.FromSeconds(3),
        Capacity = 64
    };

    [Fact]
    public async Task AWatcherEvent_LoadsTheNewPatternFile()
    {
        using var temp = new TempDirectory("edbk-watch-seam");
        var watcherFactory = new FakeDirectoryWatcherFactory();

        using var service = new PatternFileService(
            TestLoggers.For<PatternFileService>(),
            temp.Path,
            FastOptions(),
            storage: FileSystemPatternStorage.Instance,
            watcherFactory: watcherFactory,
            timeProvider: TimeProvider.System);

        var changed = new TaskCompletionSource<PatternFileChangeEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.PatternFilesChanged += args => changed.TrySetResult(args);

        var path = Path.Combine(temp.Path, "seam.json");
        await File.WriteAllTextAsync(path, ValidPatternJson);

        // Nothing has told the service about the file yet.
        Assert.Empty(service.GetAllShipTypes());

        var watcher = Assert.Single(watcherFactory.Created);
        Assert.True(watcher.Started, "the service must start the watcher it was handed");
        watcher.Raise(new WatchedFileEvent(WatchedFileChange.Created, path));
        watcher.Raise(new WatchedFileEvent(WatchedFileChange.Changed, path));

        var args = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PatternFileChangeType.Updated, args.ChangeType);
        Assert.Contains("sidewinder", service.GetAllShipTypes());
        Assert.Equal("seam.json", Assert.Single(service.GetAllPatternPacks()).FilePath);
    }

    /// <summary>Hands out watchers the test raises events on, and remembers every one it made.</summary>
    private sealed class FakeDirectoryWatcherFactory : IDirectoryWatcherFactory
    {
        private readonly List<FakeDirectoryWatcher> _created = new();

        public IReadOnlyList<FakeDirectoryWatcher> Created
        {
            get
            {
                lock (_created)
                {
                    return _created.ToList();
                }
            }
        }

        public IDirectoryWatcher Create(string directory, string filter, bool includeSubdirectories = false)
        {
            var watcher = new FakeDirectoryWatcher(directory, filter, includeSubdirectories);

            lock (_created)
            {
                _created.Add(watcher);
            }

            return watcher;
        }
    }

    /// <summary>A watcher that raises exactly the events the test asks it to, and nothing else.</summary>
    private sealed class FakeDirectoryWatcher : IDirectoryWatcher
    {
        public FakeDirectoryWatcher(string directory, string filter, bool includeSubdirectories)
        {
            Directory = directory;
            Filter = filter;
            IncludeSubdirectories = includeSubdirectories;
        }

        public event Action<WatchedFileEvent>? Changed;

        public event Action<Exception>? Error;

        public string Directory { get; }

        public string Filter { get; }

        public bool IncludeSubdirectories { get; }

        public bool Started { get; private set; }

        public bool Disposed { get; private set; }

        public void Start() => Started = true;

        public void Raise(WatchedFileEvent e) => Changed?.Invoke(e);

        public void RaiseError(Exception error) => Error?.Invoke(error);

        public void Dispose()
        {
            Disposed = true;
            Changed = null;
            Error = null;
        }
    }
}
