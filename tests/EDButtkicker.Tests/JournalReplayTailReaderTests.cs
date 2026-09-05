using System.Text.Json;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Replay reads the tail of a journal rather than the whole file. These drive the reader directly:
/// a long journal costs only the events inside the window, the window is measured from the last
/// event in the file, and a journal that is not there yields nothing rather than throwing.
/// </summary>
public class JournalReplayTailReaderTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task ReadTailAsync_OnALongJournal_ReturnsOnlyTheEventsInsideTheWindow()
    {
        using var directory = new TempDirectory("edbk-journal-tail");
        var path = directory.File("Journal.2026-01-01T000000.01.log");

        var now = DateTime.UtcNow;
        var lines = new List<string>();

        for (var i = 0; i < 5000; i++)
        {
            lines.Add(Line(now.AddHours(-1).AddMilliseconds(i), "FSDJump", $"Old {i}"));
        }

        lines.Add(Line(now.AddSeconds(-30), "FSDJump", "Recent One"));
        lines.Add(Line(now.AddSeconds(-20), "HullDamage", "Recent Two"));
        lines.Add(Line(now.AddSeconds(-10), "ShieldDown", "Recent Three"));

        await File.WriteAllLinesAsync(path, lines);

        var events = await JournalReplayTailReader.ReadTailAsync(path, Window, NullLogger.Instance);

        Assert.Equal(3, events.Count);
        Assert.Equal(new[] { "Recent One", "Recent Two", "Recent Three" }, events.Select(e => e.StarSystem));
        Assert.Equal(new[] { "FSDJump", "HullDamage", "ShieldDown" }, events.Select(e => e.Event));
    }

    [Fact]
    public async Task ReadTailAsync_OnAMissingFile_ReturnsNothing()
    {
        using var directory = new TempDirectory("edbk-journal-tail");

        var events = await JournalReplayTailReader.ReadTailAsync(
            directory.File("not-there.log"), Window, NullLogger.Instance);

        Assert.Empty(events);
    }

    [Fact]
    public async Task ReadTailAsync_WhenOnlyTheLastEventIsInsideTheWindow_ReturnsThatEventAlone()
    {
        using var directory = new TempDirectory("edbk-journal-tail");
        var path = directory.File("Journal.2026-01-02T000000.01.log");

        var now = DateTime.UtcNow;
        await File.WriteAllLinesAsync(path, new[]
        {
            Line(now.AddMinutes(-30), "Docked", "Long Ago"),
            Line(now.AddMinutes(-10), "Undocked", "Ten Minutes Ago"),
            Line(now, "FSDJump", "Now")
        });

        var events = await JournalReplayTailReader.ReadTailAsync(path, Window, NullLogger.Instance);

        var single = Assert.Single(events);
        Assert.Equal("Now", single.StarSystem);
    }

    private static string Line(DateTime timestamp, string eventName, string starSystem) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["timestamp"] = timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["event"] = eventName,
            ["StarSystem"] = starSystem
        });
}
