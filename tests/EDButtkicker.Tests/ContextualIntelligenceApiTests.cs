using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The context routes were covered only by the DI smoke test, which asks for a status code and
/// nothing else, so the page could have been reading fields the controller never wrote. These pin
/// the payload the page actually reads, and the round trip behind the two sliders on it: a config
/// POST is visible in the next status read, a slider pushed past its end stop is clamped rather
/// than refused, and a body that is not JSON is a 400.
/// </summary>
public class ContextualIntelligenceApiTests : IDisposable
{
    private readonly TempDirectory _settingsDir = new("edbk-context-api");
    private readonly SetupTestHost _host;

    public ContextualIntelligenceApiTests()
    {
        _host = new SetupTestHost(_settingsDir.Path);
    }

    [Fact]
    public async Task Status_DescribesTheConfiguration_TheLiveContextAndTheStatistics()
    {
        var body = await _host.GetJsonAsync("/api/context/status");

        // Configuration is the switch and the slider the settings page draws.
        var configuration = body.GetProperty("configuration");
        Assert.False(configuration.GetProperty("enabled").GetBoolean());
        Assert.Equal(0.1, configuration.GetProperty("learning_rate").GetDouble(), 3);

        // Current context is the live read: fresh, so a named state and no journey behind it yet.
        Assert.False(
            string.IsNullOrWhiteSpace(body.GetProperty("current_context").GetProperty("game_state").GetString()),
            "the status must name the game state, even before any journal event has arrived");
        Assert.Equal(0, body.GetProperty("statistics").GetProperty("systems_visited").GetInt32());

        // Predictions ride along in the status payload as well as on their own route.
        Assert.True(body.GetProperty("predictions").TryGetProperty("predicted_next_state", out _));
    }

    [Fact]
    public async Task Config_AppliesTheUpdate_AndTheNextStatusReadShowsIt()
    {
        var response = await PostConfigAsync("""{"enabled": true, "learning_rate": 0.5}""");
        var result = await SetupTestHost.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.True(result.GetProperty("enabled").GetBoolean());

        // The response says it took, and the next read of the status agrees - the page is not being
        // told about a change that only lived for the duration of the POST.
        var configuration = (await _host.GetJsonAsync("/api/context/status")).GetProperty("configuration");
        Assert.True(configuration.GetProperty("enabled").GetBoolean());
        Assert.Equal(0.5, configuration.GetProperty("learning_rate").GetDouble(), 3);
    }

    [Fact]
    public async Task Config_ClampsALearningRateAboveTheRange_RatherThanRefusingIt()
    {
        // The value arrives from a slider, so its end stop is a choice rather than a mistake.
        var response = await PostConfigAsync("""{"learning_rate": 2.0}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await SetupTestHost.ReadJsonAsync(response)).GetProperty("success").GetBoolean());

        var configuration = (await _host.GetJsonAsync("/api/context/status")).GetProperty("configuration");
        Assert.Equal(1.0, configuration.GetProperty("learning_rate").GetDouble(), 3);
    }

    [Fact]
    public async Task Config_ClampsALearningRateBelowTheRange_RatherThanRefusingIt()
    {
        var response = await PostConfigAsync("""{"learning_rate": -1}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var configuration = (await _host.GetJsonAsync("/api/context/status")).GetProperty("configuration");
        Assert.Equal(0.01, configuration.GetProperty("learning_rate").GetDouble(), 3);
    }

    [Theory]
    [InlineData("{ not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"just a string\"")]
    public async Task Config_WithABodyItCannotRead_IsABadRequestAndChangesNothing(string requestBody)
    {
        var response = await PostConfigAsync(requestBody);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // A refused update leaves the defaults exactly as they were.
        var configuration = (await _host.GetJsonAsync("/api/context/status")).GetProperty("configuration");
        Assert.False(configuration.GetProperty("enabled").GetBoolean());
        Assert.Equal(0.1, configuration.GetProperty("learning_rate").GetDouble(), 3);
    }

    [Fact]
    public async Task Predictions_AnswerWithTheCurrentStateAndItsConfidence()
    {
        var body = await _host.GetJsonAsync("/api/context/predictions");

        Assert.Equal(JsonValueKind.Array, body.GetProperty("predictions").ValueKind);
        Assert.False(
            string.IsNullOrWhiteSpace(body.GetProperty("current_state").GetString()),
            "the predictions payload must name the state it is predicting from");

        // Nothing has been learned yet, so the next state is genuinely unknown - the field is still
        // written, as null, because the page reads it either way.
        Assert.True(body.TryGetProperty("predicted_next_state", out var predictedNextState));
        Assert.True(
            predictedNextState.ValueKind is JsonValueKind.Null or JsonValueKind.String,
            $"predicted_next_state was {predictedNextState.ValueKind}");

        Assert.InRange(body.GetProperty("confidence").GetDouble(), 0.0, 1.0);
        Assert.False(
            string.IsNullOrWhiteSpace(
                body.GetProperty("context_factors").GetProperty("threat_level").GetString()));
    }

    [Fact]
    public async Task Predictions_FollowAConfigUpdate()
    {
        Assert.Equal(
            HttpStatusCode.OK,
            (await PostConfigAsync("""{"enabled": true, "learning_rate": 0.5}""")).StatusCode);

        // Turning the feature on must not break the route that reads from it.
        var body = await _host.GetJsonAsync("/api/context/predictions");
        Assert.Equal(JsonValueKind.Array, body.GetProperty("predictions").ValueKind);
        Assert.True(body.TryGetProperty("confidence", out _));
    }

    private Task<HttpResponseMessage> PostConfigAsync(string body) =>
        _host.Client.PostAsync("/api/context/config", new StringContent(body, Encoding.UTF8, "application/json"));

    public void Dispose()
    {
        _host.Dispose();
        _settingsDir.Dispose();
    }
}
