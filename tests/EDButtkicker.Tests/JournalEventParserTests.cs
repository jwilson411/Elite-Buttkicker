using System.Text.Json;
using EDButtkicker.Services;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The parse step between a tailed line and the event pipeline. Elite writes one JSON object per
/// line, but a line can be blank, truncated, or something other than an object - none of which may
/// stop monitoring. No file watching and no audio stack is involved here.
/// </summary>
public class JournalEventParserTests
{
    [Fact]
    public void TryParse_ReadsTheEventNameAndTimestampAsUtc()
    {
        var line = """{"timestamp":"2026-08-27T11:42:50Z","event":"FSDJump","StarSystem":"Sol"}""";

        Assert.True(JournalEventParser.TryParse(line, out var journalEvent));

        Assert.NotNull(journalEvent);
        Assert.Equal("FSDJump", journalEvent!.Event);
        Assert.Equal("Sol", journalEvent.StarSystem);
        Assert.Equal(new DateTime(2026, 8, 27, 11, 42, 50, DateTimeKind.Utc), journalEvent.Timestamp.ToUniversalTime());
    }

    [Fact]
    public void TryParse_ReadsTypedDamageFields()
    {
        var line = """{"timestamp":"2026-08-27T11:42:50Z","event":"HullDamage","Health":0.42,"PlayerPilot":true}""";

        Assert.True(JournalEventParser.TryParse(line, out var journalEvent));

        Assert.Equal("HullDamage", journalEvent!.Event);
        Assert.Equal(0.42, journalEvent.Health!.Value, 5);
    }

    [Fact]
    public void TryParse_KeepsUnmodelledFieldsAsExtensionData()
    {
        var line = """{"timestamp":"2026-08-27T11:42:50Z","event":"FuelScoop","Rate":8.5,"Scooped":12.0}""";

        Assert.True(JournalEventParser.TryParse(line, out var journalEvent));

        Assert.NotNull(journalEvent!.AdditionalData);
        Assert.True(journalEvent.AdditionalData!.ContainsKey("Rate"));
        Assert.True(journalEvent.AdditionalData.ContainsKey("Scooped"));

        // Extension data arrives as JsonElement, so the value survives parsing but is only readable
        // through the JsonElement API - not through Convert.ToDouble.
        var rate = Assert.IsType<JsonElement>(journalEvent.AdditionalData["Rate"]);
        Assert.Equal(JsonValueKind.Number, rate.ValueKind);
        Assert.Equal(8.5, rate.GetDouble(), 5);
    }

    [Fact]
    public void TryParse_IsCaseInsensitiveOnTheEventName()
    {
        Assert.True(JournalEventParser.TryParse("""{"Event":"Docked"}""", out var journalEvent));

        Assert.Equal("Docked", journalEvent!.Event);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void TryParse_SkipsBlankLines(string? line)
    {
        Assert.False(JournalEventParser.TryParse(line, out var journalEvent));
        Assert.Null(journalEvent);
    }

    [Theory]
    [InlineData("""{"event":"FSDJump" """)]      // truncated object, as seen mid-write
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]                       // an array is not an event
    [InlineData("42")]
    [InlineData("null")]
    public void TryParse_RejectsMalformedOrNonObjectLines(string line)
    {
        Assert.False(JournalEventParser.TryParse(line, out var journalEvent));
        Assert.Null(journalEvent);
    }

    [Fact]
    public void TryParse_AcceptsAnObjectWithoutAnEventName()
    {
        // Downstream treats an empty event name as "nothing to play"; parsing itself must not throw.
        Assert.True(JournalEventParser.TryParse("{}", out var journalEvent));
        Assert.Equal(string.Empty, journalEvent!.Event);
    }

    [Fact]
    public void TryParse_OneBadLineDoesNotAffectTheNext()
    {
        var lines = new[]
        {
            """{"event":"LoadGame"}""",
            "{ this line is broken",
            """{"event":"Docked","StationName":"Jameson Memorial"}"""
        };

        var parsed = new List<string>();
        foreach (var line in lines)
        {
            if (JournalEventParser.TryParse(line, out var journalEvent))
                parsed.Add(journalEvent!.Event);
        }

        Assert.Equal(new[] { "LoadGame", "Docked" }, parsed);
    }
}
