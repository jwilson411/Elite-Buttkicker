using EDButtkicker.Controllers;
using EDButtkicker.Hosting;
using EDButtkicker.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Pattern pack validation: what makes a pack rejected (errors) versus merely questionable
/// (warnings). The controller is resolved from the real service graph, but validation itself never
/// plays anything, so no audio device is involved.
/// </summary>
public class PatternValidationTests : IClassFixture<WebUiTestServerFixture>
{
    private readonly PatternEditorController _controller;

    public PatternValidationTests(WebUiTestServerFixture fixture)
    {
        _controller = fixture.Services.GetRequiredService<PatternEditorController>();
    }

    [Fact]
    public void CompletePack_IsValidWithoutWarnings()
    {
        var result = Validate(Pack());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void MissingPackNameAndAuthor_AreErrors()
    {
        var file = Pack();
        file.Metadata.Name = string.Empty;
        file.Metadata.Author = string.Empty;

        var result = Validate(file);

        Assert.False(result.IsValid);
        Assert.Contains("Pack name is required", result.Errors);
        Assert.Contains("Author is required", result.Errors);
    }

    [Fact]
    public void MissingVersion_IsOnlyAWarning()
    {
        var file = Pack();
        file.Metadata.Version = string.Empty;

        var result = Validate(file);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("Version not specified"));
    }

    [Fact]
    public void PackWithoutShips_IsValidButWarned()
    {
        var file = Pack();
        file.Ships.Clear();

        var result = Validate(file);

        Assert.True(result.IsValid);
        Assert.Contains("No ships defined in pattern file", result.Warnings);
    }

    [Fact]
    public void ShipWithoutDisplayNameOrEvents_IsWarned()
    {
        var file = Pack();
        file.Ships["anaconda"].DisplayName = string.Empty;
        file.Ships["anaconda"].Events.Clear();

        var result = Validate(file);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("has no display name"));
        Assert.Contains(result.Warnings, w => w.Contains("has no events defined"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    public void IntensityOutsideOneToOneHundred_IsAnError(int intensity)
    {
        var file = Pack();
        file.Ships["anaconda"].Events["HullDamage"].Intensity = intensity;

        var result = Validate(file);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Intensity must be between 1-100%"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(100)]
    public void IntensityOnTheBoundary_IsAccepted(int intensity)
    {
        var file = Pack();
        file.Ships["anaconda"].Events["HullDamage"].Intensity = intensity;

        var result = Validate(file);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Errors, e => e.Contains("Intensity"));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(101)]
    public void FrequencyOutsideTenToOneHundredHertz_IsAWarningNotAnError(int frequency)
    {
        var file = Pack();
        file.Ships["anaconda"].Events["HullDamage"].Frequency = frequency;

        var result = Validate(file);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("Frequency should be between 10-100Hz"));
    }

    [Fact]
    public void DurationBelowFiftyMs_IsAWarningNotAnError()
    {
        var file = Pack();
        file.Ships["anaconda"].Events["HullDamage"].Duration = 49;

        var result = Validate(file);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("Duration should be between 50-10000ms"));
    }

    [Fact]
    public void DurationOverTenThousandMs_IsAnError()
    {
        var file = Pack();
        file.Ships["anaconda"].Events["HullDamage"].Duration = 10001;

        var result = Validate(file);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Duration must not exceed 10000ms"));
    }

    [Fact]
    public void PatternWithMoreLayersThanTheCap_IsAnError()
    {
        var file = Pack();
        file.Ships["anaconda"].Events["HullDamage"].Layers =
            Enumerable.Range(0, RequestLimits.MaxPatternLayers + 1).Select(_ => new PatternLayer()).ToList();

        var result = Validate(file);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("layers"));
        Assert.Contains(result.Errors, e => e.Contains(RequestLimits.MaxPatternLayers.ToString()));
    }

    [Fact]
    public void EveryOffendingEvent_IsReportedIndividually()
    {
        var file = Pack();
        file.Ships["anaconda"].Events["HullDamage"].Intensity = 0;
        file.Ships["anaconda"].Events["ShieldDown"] = new HapticPattern
        {
            Name = "Shields Down",
            Frequency = 40,
            Duration = 500,
            Intensity = 250
        };

        var result = Validate(file);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count(e => e.Contains("Intensity must be between 1-100%")));
        Assert.Contains(result.Errors, e => e.Contains("'HullDamage'"));
        Assert.Contains(result.Errors, e => e.Contains("'ShieldDown'"));
    }

    private ValidationResponse Validate(PatternFileDefinition file)
    {
        var action = _controller.ValidatePattern(file);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        return Assert.IsType<ValidationResponse>(ok.Value);
    }

    private static PatternFileDefinition Pack() => new()
    {
        Metadata = new PatternFileMetadata
        {
            Name = "Test Pack",
            Version = "1.0.0",
            Author = "Test Author",
            Description = "Fixture pack",
            Created = "2026-08-28T12:00:00Z",
            Compatibility = "1.0.0"
        },
        Ships = new Dictionary<string, ShipPatternDefinition>
        {
            ["anaconda"] = new()
            {
                DisplayName = "Anaconda",
                Class = "Large",
                Role = "Multipurpose",
                Events = new Dictionary<string, HapticPattern>
                {
                    ["HullDamage"] = new()
                    {
                        Name = "Hull Damage",
                        Pattern = PatternType.SharpPulse,
                        Frequency = 45,
                        Duration = 300,
                        Intensity = 80
                    }
                }
            }
        }
    };
}
