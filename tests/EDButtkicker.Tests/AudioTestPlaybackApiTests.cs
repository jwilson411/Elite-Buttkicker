using System.Net;
using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Services;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The audio tab's Test button used to answer 200 as soon as playback was scheduled, so a rig with
/// no reachable output looked healthy. These pin the contract instead: the request only succeeds
/// when the tone reached an open output, the failure says which of the two things went wrong, the
/// level stays gentle, and there is a way to stop what is playing.
/// </summary>
public class AudioTestPlaybackApiTests : IDisposable
{
    private readonly TempDirectory _settingsDir = new("edbk-audio-test");

    private SetupTestHost NewHost(AppSettings settings, FakeAudioEngine engine) =>
        new(_settingsDir.Path, settings, audioEngine: engine);

    [Fact]
    public async Task Test_PlaysAQuietToneAndReportsTheOpenOutput()
    {
        var settings = new AppSettings();
        settings.Audio.AudioDeviceName = "ButtKicker Amp";
        settings.Audio.AudioDeviceEndpointId = FakeAudioDeviceCatalog.EndpointIdFor(0);
        var engine = new FakeAudioEngine(settings);
        using var host = NewHost(settings, engine);

        var response = await host.PostAsync("/api/audio/test");
        var body = await SetupTestHost.ReadJsonAsync(response);

        Assert.True(response.IsSuccessStatusCode);
        Assert.True(body.GetProperty("played").GetBoolean());

        var audio = body.GetProperty("audio");
        Assert.True(audio.GetProperty("initialized").GetBoolean());
        Assert.Equal("FakeOut", audio.GetProperty("backend").GetString());
        Assert.Equal(
            FakeAudioDeviceCatalog.EndpointIdFor(0),
            audio.GetProperty("selectedDevice").GetProperty("endpointId").GetString());
        Assert.Equal(JsonValueKind.Null, audio.GetProperty("lastPlaybackError").ValueKind);

        var played = Assert.Single(engine.Played);
        Assert.True(played.Intensity <= AudioTestPattern.IntensityPercent, "the test tone must stay quiet");
        Assert.Equal(AudioTestPattern.DurationMs, played.Duration);
        Assert.InRange(played.Frequency, AudioTestPattern.MinFrequency, AudioTestPattern.MaxFrequency);
    }

    [Fact]
    public async Task Test_WithNoOutputToReach_FailsAndNamesTheReason()
    {
        var settings = new AppSettings();
        var engine = new FakeAudioEngine(settings, canOpen: false);
        engine.FailureReason = "the ButtKicker amp is not connected";
        using var host = NewHost(settings, engine);

        var response = await host.PostAsync("/api/audio/test");
        var body = await SetupTestHost.ReadJsonAsync(response);

        // Not a 200 with a "scheduled" success: nothing was played, so the request failed.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(body.GetProperty("played").GetBoolean());
        Assert.Contains("not connected", body.GetProperty("error").GetString());
        Assert.Empty(engine.Played);

        var audio = body.GetProperty("audio");
        Assert.False(audio.GetProperty("initialized").GetBoolean());
        Assert.True(audio.GetProperty("initializationFailed").GetBoolean());
        // Nothing was opened, so naming a backend would be a guess.
        Assert.Equal(JsonValueKind.Null, audio.GetProperty("backend").ValueKind);
    }

    [Fact]
    public async Task Test_WhenAnOpenOutputRefusesTheTone_FailsAndKeepsTheError()
    {
        var settings = new AppSettings();
        var engine = new FakeAudioEngine(settings) { PlaybackFailure = "Playback failed: the mixer rejected the input" };
        using var host = NewHost(settings, engine);

        var response = await host.PostAsync("/api/audio/test");
        var body = await SetupTestHost.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(body.GetProperty("played").GetBoolean());
        Assert.Contains("the mixer rejected the input", body.GetProperty("error").GetString());
        Assert.Empty(engine.Played);

        // The device is open, and the failure is remembered as a playback error rather than as an
        // initialization one - the health payload has to be able to tell those apart.
        var audio = body.GetProperty("audio");
        Assert.True(audio.GetProperty("initialized").GetBoolean());
        Assert.Contains("the mixer rejected the input", audio.GetProperty("lastPlaybackError").GetString());

        // And it is still readable afterwards, without playing anything.
        var status = await host.GetJsonAsync("/api/audio/status");
        Assert.Contains("the mixer rejected the input", status.GetProperty("lastPlaybackError").GetString());
    }

    [Fact]
    public async Task Status_ReportsThePendingStateWithoutOpeningADevice()
    {
        var settings = new AppSettings();
        var engine = new FakeAudioEngine(settings);
        using var host = NewHost(settings, engine);

        var status = await host.GetJsonAsync("/api/audio/status");

        Assert.False(status.GetProperty("initialized").GetBoolean());
        Assert.False(status.GetProperty("initializationFailed").GetBoolean());
        Assert.Equal(0, engine.OpenAttempts);

        // The advertised test level is the capped one, not the user's maximum.
        Assert.Equal(AudioTestPattern.IntensityPercent, status.GetProperty("test").GetProperty("intensity").GetInt32());
        Assert.Equal("/api/audio/stop", status.GetProperty("test").GetProperty("stopEndpoint").GetString());
    }

    [Fact]
    public async Task Test_NeverExceedsTheConfiguredMaximum()
    {
        var settings = new AppSettings();
        settings.Audio.MaxIntensity = 10;
        var engine = new FakeAudioEngine(settings);
        using var host = NewHost(settings, engine);

        Assert.True((await host.PostAsync("/api/audio/test")).IsSuccessStatusCode);

        Assert.Equal(10, Assert.Single(engine.Played).Intensity);
    }

    [Fact]
    public async Task Stop_SilencesWhatIsPlaying()
    {
        var settings = new AppSettings();
        var engine = new FakeAudioEngine(settings);
        using var host = NewHost(settings, engine);

        Assert.True((await host.PostAsync("/api/audio/test")).IsSuccessStatusCode);

        var response = await host.PostAsync("/api/audio/stop");
        var body = await SetupTestHost.ReadJsonAsync(response);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(1, body.GetProperty("stopped").GetInt32());
        Assert.Equal(1, engine.StopRequests);
        Assert.Equal(0, body.GetProperty("audio").GetProperty("activeEffects").GetInt32());
    }

    public void Dispose() => _settingsDir.Dispose();
}
