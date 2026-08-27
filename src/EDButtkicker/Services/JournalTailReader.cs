using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EDButtkicker.Services;

/// <summary>
/// Hardware-independent tail reader for Elite Dangerous journal files.
///
/// It owns the read cursor and guarantees that:
///  - a line is only ever emitted once it is terminated by a newline (a truncated trailing
///    JSON object stays buffered in the file until the writer finishes it),
///  - the cursor is only committed through the last complete newline, so partial bytes are
///    naturally retained and re-read on the next pass,
///  - overlapping callers (e.g. two FileSystemWatcher.Changed events for the same write) are
///    serialized, so no line is emitted twice and the cursor never races.
/// </summary>
public class JournalTailReader
{
    public const string JournalSearchPattern = "Journal.*.log";

    private const int ScanChunkSize = 8192;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;
    private readonly bool _monitorLatestOnly;
    private readonly ILogger _logger;
    private readonly int _maxReadAttempts;
    private readonly TimeSpan _retryDelay;

    private string? _currentFile;
    private long _cursor;

    public JournalTailReader(
        string directory,
        bool monitorLatestOnly,
        ILogger? logger = null,
        int maxReadAttempts = 5,
        TimeSpan? retryDelay = null)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _monitorLatestOnly = monitorLatestOnly;
        _logger = logger ?? NullLogger.Instance;
        _maxReadAttempts = Math.Max(1, maxReadAttempts);
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(100);
    }

    /// <summary>Journal file the cursor currently belongs to, or null before the first attach.</summary>
    public string? CurrentFile => _currentFile;

    /// <summary>Byte offset just past the last committed newline of <see cref="CurrentFile"/>.</summary>
    public long Cursor => Interlocked.Read(ref _cursor);

    /// <summary>
    /// Returns every complete line that became available since the last call, following rotation
    /// to a newer Journal.*.log. Calls are serialized; concurrent callers each receive a disjoint
    /// slice of the stream in order.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReadNewLinesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lines = new List<string>();

            var latest = FindLatestJournalFile();
            if (latest == null)
                return lines;

            if (_currentFile == null)
            {
                _currentFile = latest;
                // MonitorLatestOnly starts at the last newline rather than at raw EOF so that a
                // trailing partial line is still completed (and emitted) once the writer finishes it.
                _cursor = _monitorLatestOnly
                    ? await FindLastNewlineOffsetAsync(latest, cancellationToken).ConfigureAwait(false)
                    : 0;

                _logger.LogInformation("Tailing journal {File} from offset {Offset}",
                    Path.GetFileName(latest), _cursor);
            }
            else if (!string.Equals(latest, _currentFile, StringComparison.Ordinal))
            {
                // Drain whatever complete lines are still sitting in the old file before switching,
                // so rotation never drops events. A new journal is a new logical stream, so any
                // partial bytes left in the old file are abandoned and the new file starts at 0.
                await ReadCompleteLinesAsync(_currentFile, lines, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Journal rotated: {Old} -> {New}",
                    Path.GetFileName(_currentFile), Path.GetFileName(latest));

                _currentFile = latest;
                _cursor = 0;
            }

            await ReadCompleteLinesAsync(_currentFile, lines, cancellationToken).ConfigureAwait(false);
            return lines;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Opens a journal file for shared reading. Overridable so tests can inject IO faults.</summary>
    protected virtual FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ScanChunkSize, useAsync: true);

    public string? FindLatestJournalFile()
    {
        try
        {
            if (!Directory.Exists(_directory))
                return null;

            // Elite journal names embed their timestamp (Journal.2026-08-27T114250.01.log), so an
            // ordinal name sort is both newest-first and deterministic - unlike creation timestamps,
            // which are unreliable on some filesystems and shift when a file is appended to.
            return Directory.GetFiles(_directory, JournalSearchPattern)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding journal files in {Path}", _directory);
            return null;
        }
    }

    private async Task ReadCompleteLinesAsync(string path, List<string> lines, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _maxReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var stream = OpenRead(path);

                var length = stream.Length;
                var cursor = Interlocked.Read(ref _cursor);

                if (length < cursor)
                {
                    // The file shrank below what we already committed: the writer restarted it
                    // (or it was rewritten). Rewind to the start and re-read the current content.
                    _logger.LogWarning("Journal {File} truncated ({Length} < {Cursor}); restarting from offset 0",
                        Path.GetFileName(path), length, cursor);
                    cursor = 0;
                    Interlocked.Exchange(ref _cursor, 0);
                }

                if (length == cursor)
                    return;

                stream.Seek(cursor, SeekOrigin.Begin);

                var pending = (int)Math.Min(length - cursor, int.MaxValue);
                var buffer = new byte[pending];
                var read = 0;
                while (read < pending)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(read, pending - read), cancellationToken)
                        .ConfigureAwait(false);
                    if (n == 0)
                        break;
                    read += n;
                }

                var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', Math.Max(read - 1, 0), read);
                if (lastNewline < 0)
                    return; // Nothing complete yet - leave the cursor where it is and retain the partial bytes.

                var committed = lastNewline + 1;
                AppendLines(buffer, committed, cursor == 0, lines);
                Interlocked.Exchange(ref _cursor, cursor + committed);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FileNotFoundException)
            {
                return; // Rotated away underneath us; the next pass picks up the new file.
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException ex)
            {
                if (attempt >= _maxReadAttempts)
                {
                    // Stay alive: the next signal retries from the same cursor, so nothing is lost.
                    _logger.LogWarning(ex, "Giving up reading {File} after {Attempts} attempts; will retry on next signal",
                        Path.GetFileName(path), attempt);
                    return;
                }

                _logger.LogDebug("Journal {File} is locked (attempt {Attempt}); retrying", Path.GetFileName(path), attempt);
                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Access denied reading journal {File}", Path.GetFileName(path));
                return;
            }
        }
    }

    private static void AppendLines(byte[] buffer, int count, bool atFileStart, List<string> lines)
    {
        var offset = 0;
        if (atFileStart && count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            offset = 3;

        // The range always ends on a newline, so it can never split a multi-byte UTF-8 sequence.
        var text = Utf8.GetString(buffer, offset, count - offset);

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(trimmed))
                lines.Add(trimmed);
        }
    }

    /// <summary>Offset just past the file's last newline, i.e. where a trailing partial line begins.</summary>
    private async Task<long> FindLastNewlineOffsetAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _maxReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var stream = OpenRead(path);

                var length = stream.Length;
                var buffer = new byte[ScanChunkSize];
                var end = length;

                while (end > 0)
                {
                    var start = Math.Max(0, end - ScanChunkSize);
                    var size = (int)(end - start);

                    stream.Seek(start, SeekOrigin.Begin);
                    var read = 0;
                    while (read < size)
                    {
                        var n = await stream.ReadAsync(buffer.AsMemory(read, size - read), cancellationToken)
                            .ConfigureAwait(false);
                        if (n == 0)
                            break;
                        read += n;
                    }

                    var idx = Array.LastIndexOf(buffer, (byte)'\n', Math.Max(read - 1, 0), read);
                    if (idx >= 0)
                        return start + idx + 1;

                    end = start;
                }

                return 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException) when (attempt < _maxReadAttempts)
            {
                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not determine tail offset for {File}; starting at 0", Path.GetFileName(path));
                return 0;
            }
        }

        return 0;
    }
}
