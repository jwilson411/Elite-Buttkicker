using System.Reflection;
using System.Text.Json;
using EDButtkicker.Services;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Pattern file watching is a burst-y, multi-threaded source: one save raises several events, and
/// editors write through temp files and renames. These pin that every event lands on one serialized
/// consumer - debounced per path, deferred until the write is finished, and cancelled by shutdown -
/// so a reload can never overlap another reload, and nothing survives Dispose.
///
/// The queue tests drive <see cref="PatternFileWatchQueue"/> directly (no FileSystemWatcher, so no
/// dependence on OS event timing); the last two run the real watcher against a temp directory.
/// </summary>
public class PatternFileWatcherDebounceTests
{
    private const string ValidPatternJson = """
    {
      "metadata": { "name": "Watch Test", "version": "1.0.0", "author": "Tester", "description": "d", "tags": [], "created": "2026-01-01T00:00:00Z", "compatibility": "1.0.0" },
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
    public async Task ABurstOfEventsForOneFile_CollapsesIntoASingleReload()
    {
        using var temp = new TempDirectory("edbk-watch-burst");
        var path = temp.File("burst.json");
        await File.WriteAllTextAsync(path, ValidPatternJson);

        var recorder = new WorkRecorder();
        using var cts = new CancellationTokenSource();
        using var queue = new PatternFileWatchQueue(recorder.Handler, FastOptions(400));
        var consumer = queue.RunAsync(cts.Token);

        // What one save through an editor looks like: create + several changes, all within the
        // debounce window.
        for (var i = 0; i < 50; i++)
        {
            queue.Enqueue(PatternWatchAction.Reload, path);
        }

        Assert.True(await WaitForAsync(() => recorder.Count >= 1, TimeSpan.FromSeconds(5)));
        await Task.Delay(600);

        Assert.Equal(1, recorder.Count);
        Assert.Equal(1, recorder.MaxConcurrency);

        await StopAsync(cts, consumer);
    }

    [Fact]
    public async Task EventsForManyFilesFromManyThreads_AreHandledOneAtATime()
    {
        using var temp = new TempDirectory("edbk-watch-serial");

        var paths = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            var path = temp.File($"pack-{i}.json");
            await File.WriteAllTextAsync(path, ValidPatternJson);
            paths.Add(path);
        }

        var recorder = new WorkRecorder(handlerDelay: TimeSpan.FromMilliseconds(20));
        using var cts = new CancellationTokenSource();
        using var queue = new PatternFileWatchQueue(recorder.Handler, FastOptions());
        var consumer = queue.RunAsync(cts.Token);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            foreach (var path in paths)
            {
                queue.Enqueue(PatternWatchAction.Reload, path);
            }
        })));

        Assert.True(await WaitForAsync(() => recorder.Count >= paths.Count, TimeSpan.FromSeconds(10)));
        await Task.Delay(400);

        // Every path exactly once, and never two handlers at the same time.
        Assert.Equal(paths.Count, recorder.Count);
        Assert.Equal(paths.Count, recorder.Snapshot().Select(w => w.Work.FullPath).Distinct().Count());
        Assert.Equal(1, recorder.MaxConcurrency);

        await StopAsync(cts, consumer);
    }

    [Fact]
    public async Task APartiallyWrittenFile_IsOnlyReadOnceTheWriteIsStable()
    {
        using var temp = new TempDirectory("edbk-watch-partial");
        var path = temp.File("partial.json");

        // First half of the document: valid JSON would not parse yet.
        var half = ValidPatternJson[..(ValidPatternJson.Length / 2)];
        await File.WriteAllTextAsync(path, half);

        var recorder = new WorkRecorder(captureContent: true);
        using var cts = new CancellationTokenSource();
        using var queue = new PatternFileWatchQueue(recorder.Handler, FastOptions());
        var consumer = queue.RunAsync(cts.Token);

        queue.Enqueue(PatternWatchAction.Reload, path);

        // The writer is still going when the first debounce window would have elapsed.
        await Task.Delay(100);
        await File.WriteAllTextAsync(path, ValidPatternJson);
        queue.Enqueue(PatternWatchAction.Reload, path);

        Assert.True(await WaitForAsync(() => recorder.Count >= 1, TimeSpan.FromSeconds(5)));
        await Task.Delay(400);

        Assert.Equal(1, recorder.Count);

        // The handler never saw the half-written document.
        var content = recorder.Snapshot().Single().Content;
        Assert.Equal(ValidPatternJson, content);
        Assert.NotNull(JsonSerializer.Deserialize<JsonElement>(content!));

        await StopAsync(cts, consumer);
    }

    [Fact]
    public async Task ARename_BecomesOneRemoveOfTheOldPathPlusAReloadOfTheNew()
    {
        using var temp = new TempDirectory("edbk-watch-rename");
        var oldPath = temp.File("before.json");
        var newPath = temp.File("after.json");
        await File.WriteAllTextAsync(newPath, ValidPatternJson);

        var recorder = new WorkRecorder();
        using var cts = new CancellationTokenSource();
        using var queue = new PatternFileWatchQueue(recorder.Handler, FastOptions());
        var consumer = queue.RunAsync(cts.Token);

        // A change to the source is already queued when the rename lands: the stale entry must not
        // race the rename's own removal.
        queue.Enqueue(PatternWatchAction.Reload, oldPath);
        queue.Enqueue(PatternWatchAction.Reload, newPath, oldPath);

        Assert.True(await WaitForAsync(() => recorder.Count >= 1, TimeSpan.FromSeconds(5)));
        await Task.Delay(400);

        var work = Assert.Single(recorder.Snapshot()).Work;
        Assert.Equal(PatternWatchAction.Reload, work.Action);
        Assert.Equal(newPath, work.FullPath);
        Assert.Equal(oldPath, work.RemovedPath);

        await StopAsync(cts, consumer);
    }

    [Fact]
    public async Task ADeleteAfterAChange_ReachesTheHandlerOnlyAsARemove()
    {
        using var temp = new TempDirectory("edbk-watch-delete");
        var path = temp.File("gone.json");

        var recorder = new WorkRecorder();
        using var cts = new CancellationTokenSource();
        using var queue = new PatternFileWatchQueue(recorder.Handler, FastOptions());
        var consumer = queue.RunAsync(cts.Token);

        queue.Enqueue(PatternWatchAction.Reload, path);
        queue.Enqueue(PatternWatchAction.Remove, path);

        Assert.True(await WaitForAsync(() => recorder.Count >= 1, TimeSpan.FromSeconds(5)));
        await Task.Delay(400);

        var work = Assert.Single(recorder.Snapshot()).Work;
        Assert.Equal(PatternWatchAction.Remove, work.Action);

        await StopAsync(cts, consumer);
    }

    [Fact]
    public async Task AFullQueue_FallsBackToAFullReloadInsteadOfDroppingChanges()
    {
        using var temp = new TempDirectory("edbk-watch-overflow");

        var recorder = new WorkRecorder();
        var options = new PatternWatchOptions
        {
            DebounceWindow = TimeSpan.FromMilliseconds(150),
            StabilityWindow = TimeSpan.FromMilliseconds(50),
            MaxStabilityWait = TimeSpan.FromSeconds(3),
            Capacity = 4
        };

        using var cts = new CancellationTokenSource();
        using var queue = new PatternFileWatchQueue(recorder.Handler, options);

        // Fill the queue before the consumer starts, so the overflow is guaranteed.
        for (var i = 0; i < 50; i++)
        {
            queue.Enqueue(PatternWatchAction.Reload, temp.File($"overflow-{i}.json"));
        }

        var consumer = queue.RunAsync(cts.Token);

        Assert.True(await WaitForAsync(
            () => recorder.Snapshot().Any(w => w.Work.Action == PatternWatchAction.ReloadAll),
            TimeSpan.FromSeconds(5)));
        Assert.Equal(1, recorder.MaxConcurrency);

        await StopAsync(cts, consumer);
    }

    [Fact]
    public async Task CancellingTheConsumer_DropsPendingDebouncedWork()
    {
        using var temp = new TempDirectory("edbk-watch-cancel");
        var path = temp.File("pending.json");
        await File.WriteAllTextAsync(path, ValidPatternJson);

        var recorder = new WorkRecorder();
        using var cts = new CancellationTokenSource();
        using var queue = new PatternFileWatchQueue(recorder.Handler, FastOptions(5_000));
        var consumer = queue.RunAsync(cts.Token);

        queue.Enqueue(PatternWatchAction.Reload, path);
        await Task.Delay(100);

        cts.Cancel();

        // The pending five-second debounce delay ends with the token, not with the clock.
        var finished = await Task.WhenAny(consumer, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(consumer, finished);
        await consumer;
        Assert.True(consumer.IsCompletedSuccessfully);
        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task TheWatcher_ReloadsRenamesAndRemovesPatternFiles()
    {
        using var temp = new TempDirectory("edbk-watch-service");
        using var service = new PatternFileService(TestLoggers.For<PatternFileService>(), temp.Path, FastOptions());

        var firstPath = Path.Combine(temp.Path, "watched.json");
        var renamedPath = Path.Combine(temp.Path, "watched-renamed.json");

        await File.WriteAllTextAsync(firstPath, ValidPatternJson);
        Assert.True(await WaitForAsync(
            () => service.GetAllShipTypes().Contains("sidewinder"), TimeSpan.FromSeconds(10)));
        Assert.Equal("watched.json", Assert.Single(service.GetAllPatternPacks()).FilePath);

        File.Move(firstPath, renamedPath);
        Assert.True(await WaitForAsync(
            () => service.GetAllPatternPacks().Any(p => p.FilePath == "watched-renamed.json"),
            TimeSpan.FromSeconds(10)));

        // The rename removed the old entry rather than leaving both packs registered.
        Assert.Equal("watched-renamed.json", Assert.Single(service.GetAllPatternPacks()).FilePath);

        File.Delete(renamedPath);
        Assert.True(await WaitForAsync(
            () => service.GetAllPatternPacks().Count == 0, TimeSpan.FromSeconds(10)));
        Assert.Empty(service.GetAllShipTypes());
    }

    [Fact]
    public async Task DisposingWhileADebounceIsPending_StopsTheConsumerAndRaisesNothingElse()
    {
        using var temp = new TempDirectory("edbk-watch-dispose");
        var service = new PatternFileService(
            TestLoggers.For<PatternFileService>(),
            temp.Path,
            FastOptions(30_000));

        var notifications = 0;
        service.PatternFilesChanged += _ => Interlocked.Increment(ref notifications);

        await File.WriteAllTextAsync(Path.Combine(temp.Path, "pending.json"), ValidPatternJson);
        await Task.Delay(300); // Let the watcher event land while the 30s debounce is pending.

        var started = DateTime.UtcNow;
        service.Dispose();
        var elapsed = DateTime.UtcNow - started;

        // Shutdown does not wait out the debounce window.
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"Dispose took {elapsed}");

        var consumer = ConsumerTaskOf(service);
        Assert.True(consumer.IsCompleted);
        Assert.True(consumer.IsCompletedSuccessfully, "The watch consumer faulted during shutdown");

        // Nothing is left to fire after dispose, and a second dispose is a no-op.
        var afterDispose = Volatile.Read(ref notifications);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "later.json"), ValidPatternJson);
        await Task.Delay(300);
        service.Dispose();

        Assert.Equal(afterDispose, Volatile.Read(ref notifications));
    }

    private static Task ConsumerTaskOf(PatternFileService service)
    {
        var field = typeof(PatternFileService)
            .GetField("_watchConsumer", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);

        return (Task)field!.GetValue(service)!;
    }

    private static async Task StopAsync(CancellationTokenSource cts, Task consumer)
    {
        cts.Cancel();
        await consumer;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    /// <summary>
    /// Records what the consumer did, including the highest number of handlers ever running at
    /// once - the whole point of the queue is that this stays at one.
    /// </summary>
    private sealed class WorkRecorder
    {
        private readonly List<HandledWork> _work = new();
        private readonly object _gate = new();
        private readonly TimeSpan _handlerDelay;
        private readonly bool _captureContent;
        private int _inFlight;
        private int _maxConcurrency;

        public WorkRecorder(TimeSpan? handlerDelay = null, bool captureContent = false)
        {
            _handlerDelay = handlerDelay ?? TimeSpan.Zero;
            _captureContent = captureContent;
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _work.Count;
                }
            }
        }

        public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

        public IReadOnlyList<HandledWork> Snapshot()
        {
            lock (_gate)
            {
                return _work.ToList();
            }
        }

        public async Task Handler(PatternWatchWork work, CancellationToken cancellationToken)
        {
            var inFlight = Interlocked.Increment(ref _inFlight);
            try
            {
                lock (_gate)
                {
                    _maxConcurrency = Math.Max(_maxConcurrency, inFlight);
                    _work.Add(new HandledWork(
                        work,
                        _captureContent && File.Exists(work.FullPath) ? File.ReadAllText(work.FullPath) : null));
                }

                if (_handlerDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_handlerDelay, cancellationToken);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private sealed record HandledWork(PatternWatchWork Work, string? Content);
}
