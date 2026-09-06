using System.Text;
using EDButtkicker.Services;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The same rotation and partial-write guarantees <see cref="JournalTailReaderTests"/> pins against
/// a real temp directory, driven through <see cref="IJournalStorage"/> instead. The point is the
/// seam, not the OS: here the test decides exactly when a byte becomes visible and when a newer
/// journal appears, so neither fact depends on how the filesystem happens to flush.
/// </summary>
public class JournalStorageSeamTests
{
    private const string FileA = "Journal.2026-08-27T114250.01.log";
    private const string FileB = "Journal.2026-08-27T120000.01.log";
    private const string Directory = "/memory/journals";

    [Fact]
    public async Task Rotation_FollowsTheNewerJournalWithoutLosingItsFirstLine()
    {
        var storage = new MemoryJournalStorage(Directory);
        storage.Write(FileA, Event("A1") + "\n");

        var reader = new JournalTailReader(Directory, monitorLatestOnly: true, storage: storage);

        // MonitorLatestOnly starts at the tail, so the pre-existing line is history.
        Assert.Empty(await reader.ReadNewLinesAsync());

        // A newer journal appears with a line already in it: rotation must emit it, not skip it.
        storage.Write(FileB, Event("B1") + "\n");

        Assert.Equal(new[] { Event("B1") }, await reader.ReadNewLinesAsync());
        Assert.Equal(Path.Combine(Directory, FileB), reader.CurrentFile);
        Assert.Empty(await reader.ReadNewLinesAsync());
    }

    [Fact]
    public async Task PartialWrite_IsOnlyEmittedOnceItsNewlineBecomesVisible()
    {
        var storage = new MemoryJournalStorage(Directory);
        storage.Write(FileA, Event("Complete") + "\n" + "{\"event\":\"Half\"}");

        var reader = new JournalTailReader(Directory, monitorLatestOnly: false, storage: storage);

        // Only the terminated line is emitted; the unterminated one stays buffered in the file.
        Assert.Equal(new[] { Event("Complete") }, await reader.ReadNewLinesAsync());
        Assert.Empty(await reader.ReadNewLinesAsync());

        storage.Append(FileA, "\n");

        Assert.Equal(new[] { Event("Half") }, await reader.ReadNewLinesAsync());
        Assert.Empty(await reader.ReadNewLinesAsync());
    }

    private static string Event(string name) => $"{{\"event\":\"{name}\"}}";

    /// <summary>
    /// A journal directory that exists only in this test. Every read hands back a fresh seekable
    /// copy of the bytes committed so far, which is exactly the guarantee the reader relies on to
    /// tell a finished line from a half-written one.
    /// </summary>
    private sealed class MemoryJournalStorage : IJournalStorage
    {
        private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private readonly string _directory;

        public MemoryJournalStorage(string directory)
        {
            _directory = directory;
        }

        public void Write(string fileName, string content) =>
            _files[Path.Combine(_directory, fileName)] = Utf8.GetBytes(content);

        public void Append(string fileName, string content)
        {
            var path = Path.Combine(_directory, fileName);
            var existing = _files.TryGetValue(path, out var bytes) ? bytes : Array.Empty<byte>();

            _files[path] = existing.Concat(Utf8.GetBytes(content)).ToArray();
        }

        public bool DirectoryExists(string directory) =>
            _files.Keys.Any(path => path.StartsWith(directory, StringComparison.Ordinal));

        /// <summary>
        /// Only the reader's own "Journal.*.log" pattern is supported, which is all it ever asks
        /// for: prefix and suffix around the single wildcard.
        /// </summary>
        public IReadOnlyList<string> ListFiles(string directory, string searchPattern)
        {
            var star = searchPattern.IndexOf('*');
            var prefix = star < 0 ? searchPattern : searchPattern[..star];
            var suffix = star < 0 ? string.Empty : searchPattern[(star + 1)..];

            return _files.Keys
                .Where(path => string.Equals(Path.GetDirectoryName(path), directory, StringComparison.Ordinal))
                .Where(path =>
                {
                    var name = Path.GetFileName(path);
                    return name.StartsWith(prefix, StringComparison.Ordinal)
                        && name.EndsWith(suffix, StringComparison.Ordinal)
                        && name.Length >= prefix.Length + suffix.Length;
                })
                .ToList();
        }

        public Stream OpenRead(string path)
        {
            if (!_files.TryGetValue(path, out var bytes))
            {
                throw new FileNotFoundException("No such journal file", path);
            }

            // A copy, so a later Append cannot change what an open stream reports as its length.
            return new MemoryStream(bytes.ToArray(), writable: false);
        }
    }
}
