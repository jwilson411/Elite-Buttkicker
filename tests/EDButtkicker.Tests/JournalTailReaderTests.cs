using System.Text;
using EDButtkicker.Services;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Journal tailing must be lossless: never emit a line that has not been terminated by a newline,
/// never emit the same line twice, and never lose complete lines across rotation or truncation.
/// Everything here drives the reader/pump seam directly - no FileSystemWatcher, no audio stack.
/// </summary>
public class JournalTailReaderTests
{
    private const string FileA = "Journal.2026-08-27T114250.01.log";
    private const string FileB = "Journal.2026-08-27T120000.01.log";

    [Fact]
    public async Task PartialLine_IsNotEmittedUntilNewlineArrives()
    {
        using var dir = new TempJournalDirectory();
        dir.Write(FileA, Event("LoadGame") + "\n" + "{\"event\":\"FSDJump\"");

        var reader = new JournalTailReader(dir.Path, monitorLatestOnly: false);

        var first = await reader.ReadNewLinesAsync();
        Assert.Equal(new[] { Event("LoadGame") }, first);

        // The truncated JSON must stay buffered - re-reading yields nothing new.
        Assert.Empty(await reader.ReadNewLinesAsync());

        dir.Append(FileA, "}\n");

        var second = await reader.ReadNewLinesAsync();
        Assert.Equal(new[] { Event("FSDJump") }, second);
        Assert.Empty(await reader.ReadNewLinesAsync());
    }

    [Fact]
    public async Task ConcurrentReads_EmitEachLineExactlyOnce()
    {
        using var dir = new TempJournalDirectory();
        dir.Write(FileA, Lines(Event("A"), Event("B"), Event("C")));

        var reader = new JournalTailReader(dir.Path, monitorLatestOnly: false);

        // Two overlapping "Changed" style reads for the same write.
        var t1 = Task.Run(() => reader.ReadNewLinesAsync());
        var t2 = Task.Run(() => reader.ReadNewLinesAsync());
        var emitted = (await t1).Concat(await t2).ToList();

        Assert.Equal(new[] { Event("A"), Event("B"), Event("C") }, emitted);
        Assert.Empty(await reader.ReadNewLinesAsync());
    }

    [Fact]
    public async Task MonitorLatestOnly_SkipsHistoryButReadsLaterAppends()
    {
        using var dir = new TempJournalDirectory();
        dir.Write(FileA, Lines(Event("Historical")));

        var reader = new JournalTailReader(dir.Path, monitorLatestOnly: true);

        Assert.Empty(await reader.ReadNewLinesAsync());

        dir.Append(FileA, Lines(Event("FSDJump")));

        Assert.Equal(new[] { Event("FSDJump") }, await reader.ReadNewLinesAsync());
    }

    [Fact]
    public async Task MonitorLatestOnly_RetainsTrailingPartialLine()
    {
        using var dir = new TempJournalDirectory();
        dir.Write(FileA, Lines(Event("Historical")) + "{\"event\":\"Part");

        var reader = new JournalTailReader(dir.Path, monitorLatestOnly: true);

        Assert.Empty(await reader.ReadNewLinesAsync());

        dir.Append(FileA, "ial\"}\n");

        Assert.Equal(new[] { Event("Partial") }, await reader.ReadNewLinesAsync());
    }

    [Fact]
    public async Task MonitorLatestOnly_False_ReadsExistingLines()
    {
        using var dir = new TempJournalDirectory();
        dir.Write(FileA, Lines(Event("A"), Event("B")));

        var reader = new JournalTailReader(dir.Path, monitorLatestOnly: false);

        Assert.Equal(new[] { Event("A"), Event("B") }, await reader.ReadNewLinesAsync());
    }

    [Fact]
    public async Task Rotation_DrainsOldFileThenReadsNewFile()
    {
        using var dir = new TempJournalDirectory();
        dir.Write(FileA, Lines(Event("A1")) + "{\"event\":\"A2\"");

        var reader = new JournalTailReader(dir.Path, monitorLatestOnly: false);
        Assert.Equal(new[] { Event("A1") }, await reader.ReadNewLinesAsync());

        // The buffered partial completes at the same moment a newer journal appears.
        dir.Append(FileA, "}\n");
        dir.Write(FileB, Lines(Event("B1")));

        var afterRotation = await reader.ReadNewLinesAsync();
        Assert.Equal(new[] { Event("A2"), Event("B1") }, afterRotation);
        Assert.Equal(Path.Combine(dir.Path, FileB), reader.CurrentFile);

        dir.Append(FileB, Lines(Event("B2")));
        Assert.Equal(new[] { Event("B2") }, await reader.ReadNewLinesAsync());
        Assert.Empty(await reader.ReadNewLinesAsync());
    }

    [Fact]
    public async Task Truncation_RewindsAndReadsRewrittenContent()
    {
        using var dir = new TempJournalDirectory();
        dir.Write(FileA, Lines(Event("A"), Event("B")));

        var reader = new JournalTailReader(dir.Path, monitorLatestOnly: false);
        Assert.Equal(new[] { Event("A"), Event("B") }, await reader.ReadNewLinesAsync());

        // File shrinks below the committed cursor - treat it as a restarted stream.
        dir.Write(FileA, Lines(Event("AfterTruncate")));

        Assert.Equal(new[] { Event("AfterTruncate") }, await reader.ReadNewLinesAsync());
        Assert.Empty(await reader.ReadNewLinesAsync());
    }

    [Fact]
    public async Task LockedFile_IsRetriedRatherThanTearingDown()
    {
        using var dir = new TempJournalDirectory();
        dir.Write(FileA, Lines(Event("Locked")));

        var reader = new FlakyOpenJournalTailReader(dir.Path, failuresBeforeSuccess: 3);

        Assert.Equal(new[] { Event("Locked") }, await reader.ReadNewLinesAsync());
        Assert.True(reader.OpenAttempts > 1, "reader should have retried the failed opens");
    }

    [Fact]
    public async Task ExclusivelyLockedFile_IsReadOnceTheLockIsReleased()
    {
        using var dir = new TempJournalDirectory();
        dir.Write(FileA, Lines(Event("Unlocked")));

        var reader = new JournalTailReader(dir.Path, monitorLatestOnly: false,
            maxReadAttempts: 40, retryDelay: TimeSpan.FromMilliseconds(25));

        var locker = new FileStream(Path.Combine(dir.Path, FileA), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(150);
            locker.Dispose();
        });

        var lines = await reader.ReadNewLinesAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await release;

        Assert.Equal(new[] { Event("Unlocked") }, lines);
    }

    [Fact]
    public async Task ReadNewLinesAsync_HonorsCancellation()
    {
        using var dir = new TempJournalDirectory();
        dir.Write(FileA, Lines(Event("A")));

        var reader = new JournalTailReader(dir.Path, monitorLatestOnly: false);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadNewLinesAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task EmptyDirectory_YieldsNothing()
    {
        using var dir = new TempJournalDirectory();
        var reader = new JournalTailReader(dir.Path, monitorLatestOnly: false);

        Assert.Empty(await reader.ReadNewLinesAsync());
        Assert.Null(reader.CurrentFile);
    }

    [Fact]
    public async Task Pump_SerializesOverlappingSignals()
    {
        var entered = new SemaphoreSlim(0);
        var release = new SemaphoreSlim(0);
        var concurrent = 0;
        var maxConcurrent = 0;
        var invocations = 0;

        using var pump = new JournalSignalPump(async _ =>
        {
            var now = Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, now);
            Interlocked.Increment(ref invocations);
            entered.Release();
            await release.WaitAsync();
            Interlocked.Decrement(ref concurrent);
        });

        using var cts = new CancellationTokenSource();
        var run = pump.RunAsync(cts.Token);

        pump.Signal();
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));

        // Second signal arrives while the first handler is still running.
        pump.Signal();
        Assert.False(await entered.WaitAsync(TimeSpan.FromMilliseconds(200)), "handlers must not overlap");

        release.Release();
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
        release.Release();

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, invocations);
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task Pump_StopsOnCancellationWithoutHanging()
    {
        using var pump = new JournalSignalPump(_ => Task.CompletedTask);
        using var cts = new CancellationTokenSource();

        var run = pump.RunAsync(cts.Token);
        pump.Signal();
        cts.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(run.IsCompleted);
    }

    [Fact]
    public async Task Pump_KeepsRunningWhenHandlerThrows()
    {
        var succeeded = new TaskCompletionSource();
        var calls = 0;

        using var pump = new JournalSignalPump(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new IOException("boom");

            succeeded.TrySetResult();
            return Task.CompletedTask;
        });

        using var cts = new CancellationTokenSource();
        var run = pump.RunAsync(cts.Token);

        pump.Signal();
        await Task.Delay(50);
        pump.Signal();

        await succeeded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static string Event(string name) => $"{{\"event\":\"{name}\"}}";

    private static string Lines(params string[] lines) =>
        string.Concat(lines.Select(l => l + "\n"));

    /// <summary>Reader whose first N opens fail with IOException, mimicking a briefly locked file.</summary>
    private sealed class FlakyOpenJournalTailReader : JournalTailReader
    {
        private readonly int _failuresBeforeSuccess;
        private int _openAttempts;

        public FlakyOpenJournalTailReader(string directory, int failuresBeforeSuccess)
            : base(directory, monitorLatestOnly: false, maxReadAttempts: 10,
                   retryDelay: TimeSpan.FromMilliseconds(1))
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public int OpenAttempts => _openAttempts;

        protected override FileStream OpenRead(string path)
        {
            if (Interlocked.Increment(ref _openAttempts) <= _failuresBeforeSuccess)
                throw new IOException("file is locked");

            return base.OpenRead(path);
        }
    }

    private sealed class TempJournalDirectory : IDisposable
    {
        public TempJournalDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "edbk-journal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Write(string fileName, string content) =>
            File.WriteAllText(System.IO.Path.Combine(Path, fileName), content, new UTF8Encoding(false));

        public void Append(string fileName, string content) =>
            File.AppendAllText(System.IO.Path.Combine(Path, fileName), content, new UTF8Encoding(false));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* best effort cleanup */ }
        }
    }
}
