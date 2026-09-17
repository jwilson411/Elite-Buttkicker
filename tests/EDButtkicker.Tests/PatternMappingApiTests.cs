using System.Net;
using System.Text;
using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The pattern API's three mutations used to answer 200 without touching anything, so the page
/// could not tell a saved edit from a lost one. These pin the opposite: a success means the live
/// mappings hold the edit and the file on disk does too, and anything that did not happen says so
/// with a status code - 409 for an event that is already mapped, 404 for one that is not mapped at
/// all, 400 for a pattern that cannot be read.
/// </summary>
public class PatternMappingApiTests : IDisposable
{
    /// <summary>Mapped in the built-in catalogue, so it is the target for update, delete and conflict.</summary>
    private const string MappedEvent = "FSDJump";

    /// <summary>Mapped by nothing, so create works on it and update/delete cannot.</summary>
    private const string UnmappedEvent = "EbkTestEvent";

    private readonly TempDirectory _settingsDir = new("edbk-pattern-api");
    private readonly SetupTestHost _host;

    public PatternMappingApiTests()
    {
        _host = new SetupTestHost(_settingsDir.Path);
    }

    private EventMappingService Mappings => _host.Services.GetRequiredService<EventMappingService>();

    [Fact]
    public async Task Create_StoresTheMapping_AndAnswersWithWhatWasStored()
    {
        var response = await PostAsync("/api/patterns", new
        {
            eventType = UnmappedEvent,
            pattern = Pattern("Cargo Thump", intensity: 64)
        });
        var body = await SetupTestHost.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(body.GetProperty("success").GetBoolean());

        // The response describes the stored mapping, not the request that was sent.
        var mapping = body.GetProperty("mapping");
        Assert.Equal(UnmappedEvent, mapping.GetProperty("EventType").GetString());
        Assert.True(mapping.GetProperty("Enabled").GetBoolean());
        Assert.Equal("Cargo Thump", mapping.GetProperty("Pattern").GetProperty("Name").GetString());
        Assert.Equal(64, mapping.GetProperty("Pattern").GetProperty("Intensity").GetInt32());
        Assert.Equal("Impact", mapping.GetProperty("Pattern").GetProperty("PatternType").GetString());

        // The service really holds it, the list really shows it, and it really reached the file.
        var stored = Mappings.GetEventMapping(UnmappedEvent);
        Assert.NotNull(stored);
        Assert.Equal("Cargo Thump", stored!.Pattern.Name);
        Assert.Equal("Cargo Thump", (await ListedPatternAsync(UnmappedEvent))!.Value
            .GetProperty("Pattern").GetProperty("Name").GetString());
        Assert.Equal("Cargo Thump", PersistedMappings().EventMappings[UnmappedEvent].Pattern.Name);
    }

    [Fact]
    public async Task Create_HonoursAnExplicitEnabledFlag()
    {
        var response = await PostAsync("/api/patterns", new
        {
            eventType = UnmappedEvent,
            pattern = Pattern("Quiet Thump"),
            enabled = false
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False(Mappings.GetEventMapping(UnmappedEvent)!.Enabled);
        Assert.False(PersistedMappings().EventMappings[UnmappedEvent].Enabled);
    }

    [Fact]
    public async Task Create_ForAnAlreadyMappedEvent_IsAConflictAndChangesNothing()
    {
        var before = Mappings.GetEventMapping(MappedEvent)!.Pattern.Name;

        var response = await PostAsync("/api/patterns", new
        {
            eventType = MappedEvent,
            pattern = Pattern("Overwritten")
        });
        var body = await SetupTestHost.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(MappedEvent, body.GetProperty("error").GetString());

        // Update is the path for an existing event: the stored pattern is untouched.
        Assert.Equal(before, Mappings.GetEventMapping(MappedEvent)!.Pattern.Name);
        Assert.False(File.Exists(Mappings.MappingsFilePath), "a refused create must not write a file");
    }

    [Theory]
    [InlineData("{ not json at all")]
    [InlineData("""{"eventType":"EbkTestEvent"}""")]
    [InlineData("""{"eventType":"","pattern":{"name":"Nameless"}}""")]
    [InlineData("""{"eventType":"EbkTestEvent","pattern":"SharpPulse"}""")]
    [InlineData("""{"eventType":"EbkTestEvent","pattern":{"duration":"as long as it takes"}}""")]
    [InlineData("""{"eventType":"EbkTestEvent","pattern":{"name":"Too Long","duration":600000}}""")]
    public async Task Create_WithAPatternItCannotStore_IsABadRequest(string requestBody)
    {
        var response = await _host.Client.PostAsync("/api/patterns", Json(requestBody));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(Mappings.GetEventMapping(UnmappedEvent));
        Assert.False(File.Exists(Mappings.MappingsFilePath), "a rejected create must not write a file");
    }

    [Fact]
    public async Task Update_ReplacesTheStoredPattern_AndAnswersWithWhatWasStored()
    {
        var response = await PutAsync($"/api/patterns/{MappedEvent}", Pattern("Rebuilt Arrival", intensity: 33));
        var body = await SetupTestHost.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("success").GetBoolean());

        var pattern = body.GetProperty("mapping").GetProperty("Pattern");
        Assert.Equal("Rebuilt Arrival", pattern.GetProperty("Name").GetString());
        Assert.Equal(33, pattern.GetProperty("Intensity").GetInt32());

        var stored = Mappings.GetEventMapping(MappedEvent);
        Assert.Equal("Rebuilt Arrival", stored!.Pattern.Name);
        Assert.Equal(33, stored.Pattern.Intensity);

        // Unstated fields come from the request, not from the pattern that was replaced.
        Assert.Equal(750, stored.Pattern.Duration);
        Assert.True(stored.Enabled, "an update that says nothing about enabled keeps the mapping's own value");

        Assert.Equal("Rebuilt Arrival", PersistedMappings().EventMappings[MappedEvent].Pattern.Name);
        Assert.Equal("Rebuilt Arrival", (await ListedPatternAsync(MappedEvent))!.Value
            .GetProperty("Pattern").GetProperty("Name").GetString());
    }

    [Fact]
    public async Task Update_AcceptsTheSameEnvelopeAsCreate()
    {
        var response = await PutAsync(
            $"/api/patterns/{MappedEvent}",
            new { eventType = MappedEvent, pattern = Pattern("Enveloped"), enabled = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = Mappings.GetEventMapping(MappedEvent)!;
        Assert.Equal("Enveloped", stored.Pattern.Name);
        Assert.False(stored.Enabled);
    }

    [Fact]
    public async Task Update_WithAnEnvelopeNamingADifferentEvent_IsABadRequest()
    {
        var response = await PutAsync(
            $"/api/patterns/{MappedEvent}",
            new { eventType = "Docked", pattern = Pattern("Wrong Target") });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual("Wrong Target", Mappings.GetEventMapping(MappedEvent)!.Pattern.Name);
        Assert.NotEqual("Wrong Target", Mappings.GetEventMapping("Docked")!.Pattern.Name);
    }

    [Fact]
    public async Task Update_ForAnUnmappedEvent_IsNotFoundAndCreatesNothing()
    {
        var response = await PutAsync($"/api/patterns/{UnmappedEvent}", Pattern("Nowhere To Put This"));
        var body = await SetupTestHost.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(UnmappedEvent, body.GetProperty("error").GetString());

        // Update has a target or it has nothing to do - it is not a create.
        Assert.Null(Mappings.GetEventMapping(UnmappedEvent));
        Assert.False(File.Exists(Mappings.MappingsFilePath), "a 404 update must not write a file");
    }

    [Fact]
    public async Task Update_WithAPatternItCannotRead_IsABadRequestAndChangesNothing()
    {
        var before = Mappings.GetEventMapping(MappedEvent)!.Pattern.Name;

        var response = await _host.Client.PutAsync(
            $"/api/patterns/{MappedEvent}",
            Json("""{"name":"Broken","duration":"whenever"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, Mappings.GetEventMapping(MappedEvent)!.Pattern.Name);
    }

    [Fact]
    public async Task Toggle_Enabled_FlipsTheFlagAndPersists()
    {
        var before = Mappings.GetEventMapping(MappedEvent)!;
        Assert.True(before.Enabled, $"{MappedEvent} is expected to start enabled");

        var response = await PatchAsync($"/api/patterns/{MappedEvent}/enabled", new { enabled = false });
        var body = await SetupTestHost.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.False(body.GetProperty("enabled").GetBoolean());

        // Off, stored, on disk - and the pattern itself carried through untouched, since the
        // request never sent one.
        var stored = Mappings.GetEventMapping(MappedEvent)!;
        Assert.False(stored.Enabled);
        Assert.Equal(before.Pattern.Name, stored.Pattern.Name);
        Assert.False(PersistedMappings().EventMappings[MappedEvent].Enabled);
        Assert.False((await ListedPatternAsync(MappedEvent))!.Value.GetProperty("Enabled").GetBoolean());

        // And back on again: the flag follows the body, it does not just flip once.
        Assert.Equal(
            HttpStatusCode.OK,
            (await PatchAsync($"/api/patterns/{MappedEvent}/enabled", new { enabled = true })).StatusCode);
        Assert.True(Mappings.GetEventMapping(MappedEvent)!.Enabled);
        Assert.True(PersistedMappings().EventMappings[MappedEvent].Enabled);
    }

    [Fact]
    public async Task Toggle_Enabled_WithUnknownEvent_Returns404()
    {
        var response = await PatchAsync($"/api/patterns/{UnmappedEvent}/enabled", new { enabled = false });
        var body = await SetupTestHost.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(UnmappedEvent, body.GetProperty("error").GetString());
        Assert.Null(Mappings.GetEventMapping(UnmappedEvent));
        Assert.False(File.Exists(Mappings.MappingsFilePath), "a 404 toggle must not write a file");
    }

    [Theory]
    [InlineData("{ not json at all")]
    [InlineData("{}")]
    [InlineData("""{"enabled":"false"}""")]
    [InlineData("""{"enabled":null}""")]
    public async Task Toggle_Enabled_WithMissingField_Returns400(string requestBody)
    {
        var response = await _host.Client.PatchAsync($"/api/patterns/{MappedEvent}/enabled", Json(requestBody));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(Mappings.GetEventMapping(MappedEvent)!.Enabled);
        Assert.False(File.Exists(Mappings.MappingsFilePath), "a rejected toggle must not write a file");
    }

    [Fact]
    public async Task Delete_RemovesTheMapping_AndPersistsTheRemoval()
    {
        var response = await _host.Client.DeleteAsync($"/api/patterns/{MappedEvent}");
        var body = await SetupTestHost.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("success").GetBoolean());

        Assert.Null(Mappings.GetEventMapping(MappedEvent));
        Assert.Null(await ListedPatternAsync(MappedEvent));
        Assert.DoesNotContain(MappedEvent, PersistedMappings().EventMappings.Keys);
        Assert.Equal(Mappings.GetEventMappings().Count, body.GetProperty("remainingPatterns").GetInt32());
    }

    [Fact]
    public async Task Delete_ForAnUnmappedEvent_IsNotFound()
    {
        var response = await _host.Client.DeleteAsync($"/api/patterns/{UnmappedEvent}");
        var body = await SetupTestHost.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(UnmappedEvent, body.GetProperty("error").GetString());
        Assert.False(File.Exists(Mappings.MappingsFilePath), "a 404 delete must not write a file");
    }

    [Fact]
    public async Task Delete_ThenCreate_MapsTheEventAgain()
    {
        Assert.Equal(HttpStatusCode.OK, (await _host.Client.DeleteAsync($"/api/patterns/{MappedEvent}")).StatusCode);

        // With nothing mapped to it any more, the event is a create rather than a conflict.
        var response = await PostAsync("/api/patterns", new
        {
            eventType = MappedEvent,
            pattern = Pattern("Second Arrival")
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Second Arrival", Mappings.GetEventMapping(MappedEvent)!.Pattern.Name);
        Assert.Equal("Second Arrival", PersistedMappings().EventMappings[MappedEvent].Pattern.Name);
    }

    [Fact]
    public async Task EditsSurviveARestart()
    {
        Assert.Equal(HttpStatusCode.Created, (await PostAsync("/api/patterns", new
        {
            eventType = UnmappedEvent,
            pattern = Pattern("Kept Across Restarts")
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _host.Client.DeleteAsync($"/api/patterns/{MappedEvent}")).StatusCode);

        // A second process pointed at the same settings directory, loading as Program does.
        using var restarted = new SetupTestHost(_settingsDir.Path);
        var mappings = restarted.Services.GetRequiredService<EventMappingService>();
        mappings.LoadSavedEventMappings();

        Assert.Equal("Kept Across Restarts", mappings.GetEventMapping(UnmappedEvent)!.Pattern.Name);
        Assert.Null(mappings.GetEventMapping(MappedEvent));
    }

    /// <summary>The pattern body the page sends: camelCase fields, enums by name.</summary>
    private static object Pattern(string name, int intensity = 55) => new
    {
        name,
        pattern = "Impact",
        frequency = 42,
        duration = 750,
        intensity,
        fadeIn = 10,
        fadeOut = 100,
        intensityCurve = "Exponential"
    };

    private Task<HttpResponseMessage> PostAsync(string path, object body) =>
        _host.Client.PostAsync(path, Json(JsonSerializer.Serialize(body)));

    private Task<HttpResponseMessage> PutAsync(string path, object body) =>
        _host.Client.PutAsync(path, Json(JsonSerializer.Serialize(body)));

    private Task<HttpResponseMessage> PatchAsync(string path, object body) =>
        _host.Client.PatchAsync(path, Json(JsonSerializer.Serialize(body)));

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>The event as GET /api/patterns lists it, or null when it lists no such event.</summary>
    private async Task<JsonElement?> ListedPatternAsync(string eventType)
    {
        var patterns = (await _host.GetJsonAsync("/api/patterns")).GetProperty("patterns");

        return patterns.TryGetProperty(eventType, out var listed) ? listed : null;
    }

    /// <summary>What the service actually wrote to disk, read back the way a restart reads it.</summary>
    private EventMappingsConfig PersistedMappings()
    {
        var path = Mappings.MappingsFilePath;
        Assert.True(File.Exists(path), $"expected the mappings to be written to {path}");

        var config = JsonSerializer.Deserialize<EventMappingsConfig>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(config);
        return config!;
    }

    public void Dispose()
    {
        _host.Dispose();
        _settingsDir.Dispose();
    }
}
