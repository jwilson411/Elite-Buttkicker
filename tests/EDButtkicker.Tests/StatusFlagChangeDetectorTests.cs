using EDButtkicker.Services;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The bit logic behind Status.json haptics, with no file, no watcher and no audio in the way.
/// The case that matters most here is a Flags2-only edge: the first implementation computed the
/// changed mask from Flags alone, so an on-foot or environment change could never fire.
/// </summary>
public class StatusFlagChangeDetectorTests
{
    private const long LandingGearDown = 1L << 2;
    private const long HardpointsDeployed = 1L << 6;
    private const long NightVision = 1L << 28;
    private const long Overheating = 1L << 20;

    private const long OnFoot2 = 1L << 0;
    private const long InTaxi2 = 1L << 1;
    private const long LowOxygen2 = 1L << 6;
    private const long LowHealth2 = 1L << 7;
    private const long VeryHot2 = 1L << 11;
    private const long GlideMode2 = 1L << 12;
    private const long FsdJump2 = 1L << 19;

    [Fact]
    public void NoChange_ProducesNoEvents()
    {
        Assert.Empty(StatusFlagChangeDetector.Detect(LandingGearDown, LandingGearDown, LowOxygen2, LowOxygen2));
    }

    [Fact]
    public void Flags2OnlyChange_IsNotIgnored()
    {
        // Flags identical on both sides - the whole edge lives in Flags2.
        var events = StatusFlagChangeDetector.Detect(LandingGearDown, LandingGearDown, 0, LowOxygen2);

        Assert.Equal(new[] { "LowOxygen" }, events);
    }

    [Theory]
    [InlineData(OnFoot2, "OnFoot")]
    [InlineData(LowOxygen2, "LowOxygen")]
    [InlineData(LowHealth2, "LowHealth")]
    [InlineData(1L << 8, "Cold")]
    [InlineData(1L << 10, "VeryCold")]
    [InlineData(1L << 9, "Hot")]
    [InlineData(VeryHot2, "VeryHot")]
    [InlineData(FsdJump2, "FsdJumpInProgress")]
    public void Flags2Bit_MapsToItsEventName(long bit, string expected)
    {
        Assert.Equal(new[] { expected }, StatusFlagChangeDetector.Detect(0, 0, 0, bit));
    }

    [Fact]
    public void Flags2Warnings_OnlyFireOnTheRisingEdge()
    {
        Assert.Equal(new[] { "VeryHot" }, StatusFlagChangeDetector.Detect(0, 0, 0, VeryHot2));
        Assert.Empty(StatusFlagChangeDetector.Detect(0, 0, VeryHot2, 0));
    }

    [Fact]
    public void GlideMode_ReportsBothDirections()
    {
        Assert.Equal(new[] { "GlideModeOn" }, StatusFlagChangeDetector.Detect(0, 0, 0, GlideMode2));
        Assert.Equal(new[] { "GlideModeOff" }, StatusFlagChangeDetector.Detect(0, 0, GlideMode2, 0));
    }

    [Fact]
    public void FlagsAndFlags2_ChangingTogetherBothReport()
    {
        var events = StatusFlagChangeDetector.Detect(0, LandingGearDown, 0, LowHealth2);

        Assert.Contains("LandingGearDown", events);
        Assert.Contains("LowHealth", events);
    }

    [Fact]
    public void ShipToggles_ReportBothDirections()
    {
        Assert.Equal(new[] { "LandingGearDown" }, StatusFlagChangeDetector.Detect(0, LandingGearDown, 0, 0));
        Assert.Equal(new[] { "LandingGearUp" }, StatusFlagChangeDetector.Detect(LandingGearDown, 0, 0, 0));
        Assert.Equal(new[] { "HardpointsRetracted" }, StatusFlagChangeDetector.Detect(HardpointsDeployed, 0, 0, 0));
        Assert.Equal(new[] { "NightVisionOff" }, StatusFlagChangeDetector.Detect(NightVision, 0, 0, 0));
    }

    [Fact]
    public void ShipWarnings_OnlyFireOnTheRisingEdge()
    {
        Assert.Equal(new[] { "Overheating" }, StatusFlagChangeDetector.Detect(0, Overheating, 0, 0));
        Assert.Empty(StatusFlagChangeDetector.Detect(Overheating, 0, 0, 0));
    }

    [Fact]
    public void UnmappedBits_ProduceNothing()
    {
        // In a taxi is a real Flags2 edge with no haptic meaning - it must stay silent rather than
        // producing an event name nothing maps.
        Assert.Empty(StatusFlagChangeDetector.Detect(0, 0, 0, InTaxi2));

        // Same for a Flags bit the monitor does not act on (Lights on, bit 8).
        Assert.Empty(StatusFlagChangeDetector.Detect(0, 1L << 8, 0, 0));
    }
}
