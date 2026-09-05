using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EDButtkicker.Models;
using Microsoft.Extensions.Logging;

namespace EDButtkicker.Services;

/// <summary>
/// Reads the tail of an Elite Dangerous journal - NDJSON, one event per line - without ever holding
/// the whole file. The file is scanned backwards in chunks and only the events inside the replay
/// window are kept, so a multi-hour journal costs the same as a short one.
/// </summary>
public static class JournalReplayTailReader
{
    /// <summary>Events kept at most, so a journal written entirely inside the window stays bounded.</summary>
    public const int MaxEvents = 10_000;

    private const int ChunkSize = 64 * 1024;

    /// <summary>A line longer than this is not a journal line; the scan stops rather than buffer it.</summary>
    private const int MaxLineBytes = 1_048_576;

    private static readonly JsonSerializerOptions JournalJson = new() { MaxDepth = 32 };

    /// <summary>
    /// Events from the last <paramref name="window"/> of the journal's own timeline: the window ends
    /// at the last event in the file, which is the same rule the whole-file read used. Journals are
    /// written in order, so the backward scan stops at the first event older than the window.
    /// The returned list is chronological. The caller must pass a path that
    /// <see cref="JournalFileGuard.Resolve"/> has already validated.
    /// </summary>
    public static async Task<List<JournalEvent>> ReadTailAsync(
        string fullPath,
        TimeSpan window,
        ILogger logger,
        int maxEvents = MaxEvents,
        CancellationToken cancellationToken = default)
    {
        var events = new List<JournalEvent>();

        if (!File.Exists(fullPath))
        {
            logger.LogWarning("Journal file not found: {FilePath}", fullPath);
            return events;
        }

        await using var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ChunkSize, useAsync: true);

        DateTime? cutoff = null;

        await foreach (var line in ReadLinesBackwardAsync(stream, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JournalEvent? journalEvent;
            try
            {
                journalEvent = JsonSerializer.Deserialize<JournalEvent>(line, JournalJson);
            }
            catch (JsonException ex)
            {
                logger.LogDebug("Failed to parse journal line: {Line} - {Error}", line, ex.Message);
                continue;
            }

            if (journalEvent == null) continue;

            // The last event in the file defines the window, exactly as before.
            cutoff ??= journalEvent.Timestamp - window;

            if (journalEvent.Timestamp < cutoff) break;

            events.Add(journalEvent);

            if (events.Count >= maxEvents)
            {
                logger.LogWarning(
                    "Journal tail reached the {MaxEvents} event limit; older events in the window were not read",
                    maxEvents);
                break;
            }
        }

        events.Reverse();
        return events;
    }

    /// <summary>
    /// The file's lines from last to first. Chunks are split on the newline byte before they are
    /// decoded, so a UTF-8 character that straddles a chunk boundary is still decoded whole.
    /// </summary>
    private static async IAsyncEnumerable<string> ReadLinesBackwardAsync(
        FileStream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[ChunkSize];
        var carry = Array.Empty<byte>();
        var position = stream.Length;

        while (position > 0)
        {
            var toRead = (int)Math.Min(ChunkSize, position);
            position -= toRead;

            stream.Seek(position, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(buffer.AsMemory(0, toRead), cancellationToken);

            var end = toRead;
            for (var i = toRead - 1; i >= 0; i--)
            {
                if (buffer[i] != (byte)'\n') continue;

                yield return Decode(Concat(buffer, i + 1, end, carry));
                carry = Array.Empty<byte>();
                end = i;
            }

            carry = Concat(buffer, 0, end, carry);

            if (carry.Length > MaxLineBytes)
            {
                yield break;
            }
        }

        if (carry.Length > 0)
        {
            yield return Decode(carry);
        }
    }

    private static byte[] Concat(byte[] buffer, int start, int end, byte[] tail)
    {
        var length = end - start;
        var joined = new byte[length + tail.Length];

        Buffer.BlockCopy(buffer, start, joined, 0, length);
        Buffer.BlockCopy(tail, 0, joined, length, tail.Length);

        return joined;
    }

    private static string Decode(byte[] line) => Encoding.UTF8.GetString(line).TrimEnd('\r');
}
