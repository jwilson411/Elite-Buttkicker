using System.Net;
using System.Text;
using System.Text.Json;
using EDButtkicker.Controllers;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Everything an API writes is read by a browser page, so a failure body may carry a stable
/// sentence and nothing else: no exception text, no stack frame, no local filesystem path. These
/// drive real 4xx and 5xx responses through the same pipeline Program runs and read the raw JSON.
/// Nothing here touches audio hardware or binds a port.
/// </summary>
public class ApiErrorSanitizationTests : IClassFixture<WebUiTestServerFixture>
{
    /// <summary>Fragments of the exception text .NET produces for the failures these tests force.</summary>
    private static readonly string[] ExceptionGiveaways =
    {
        "Could not find file",
        "Could not find a part of the path",
        "UnauthorizedAccess",
        "Access to the path",
        "is denied",
        "at EDButtkicker",
        "System.IO.",
        "Exception"
    };

    private readonly WebUiTestServerFixture _fixture;

    public ApiErrorSanitizationTests(WebUiTestServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    public async Task SavePattern_RejectedName_AnswersWithoutPathsOrExceptionText(string fileName)
    {
        var response = await _fixture.Client.PostAsync(
            "/api/PatternEditor/save",
            new StringContent(SaveRequestBody(fileName), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertSanitized(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("..%2f..%2fetc%2fpasswd")]
    [InlineData("%2e%2e%2fsecret.json")]
    public async Task LoadPattern_RejectedName_AnswersWithoutPathsOrExceptionText(string fileName)
    {
        var response = await _fixture.Client.GetAsync($"/api/PatternEditor/load/{fileName}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertSanitized(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LoadPattern_ForAFileThatIsNotThere_SaysSoWithoutTheSearchedPaths()
    {
        var response = await _fixture.Client.GetAsync("/api/PatternEditor/load/edbk-sanitize-missing.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        AssertSanitized(body);
        // The name the caller asked for is theirs already; the directories searched are not.
        Assert.Contains("edbk-sanitize-missing.json", body);
    }

    [Fact]
    public async Task JournalReplay_ForAFileOutsideTheJournalFolder_AnswersWithoutPathsOrExceptionText()
    {
        var response = await _fixture.Client.PostAsync(
            "/api/journal/replay/start",
            new StringContent("""{"journalFile":"../../etc/passwd"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertSanitized(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SetupJournalFolder_ThatDoesNotExist_DoesNotEchoTheAbsolutePathBack()
    {
        var missingFolder = Path.Combine(Path.GetTempPath(), $"edbk-sanitize-{Guid.NewGuid():N}");
        var body = JsonSerializer.Serialize(new { path = missingFolder });

        var response = await _fixture.Client.PostAsync(
            "/api/setup/journal", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(missingFolder, payload);
        Assert.DoesNotContain(JsonEncode(missingFolder), payload);
        AssertSanitized(payload);
    }

    [Fact]
    public async Task SavePattern_ThatCannotBeWritten_ReportsAStableFailure()
    {
        // A directory standing where the file has to go makes the write throw an exception whose
        // own message carries the absolute path. Nothing of it may reach the response.
        var fileName = $"edbk-sanitize-{Guid.NewGuid():N}.json";
        var blockingDirectory = Path.Combine(Directory.GetCurrentDirectory(), "patterns", "Custom", fileName);
        Directory.CreateDirectory(blockingDirectory);

        try
        {
            var response = await _fixture.Client.PostAsync(
                "/api/PatternEditor/save",
                new StringContent(SaveRequestBody(fileName), Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            AssertSanitized(body);
            Assert.Contains("Failed to save pattern file", body);
        }
        finally
        {
            Directory.Delete(blockingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SavePattern_OnSuccess_ReportsTheFileRelativeToThePatternsRoot()
    {
        var fileName = $"edbk-sanitize-{Guid.NewGuid():N}.json";
        var savedPath = Path.Combine(Directory.GetCurrentDirectory(), "patterns", "Custom", fileName);

        try
        {
            var response = await _fixture.Client.PostAsync(
                "/api/PatternEditor/save",
                new StringContent(SaveRequestBody(fileName), Encoding.UTF8, "application/json"));

            Assert.True(response.IsSuccessStatusCode, $"save returned {(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync();
            AssertSanitized(body);

            using var document = JsonDocument.Parse(body);
            var reported = document.RootElement.GetProperty("filePath").GetString();

            Assert.False(Path.IsPathRooted(reported));
            Assert.Equal(Path.Combine("Custom", fileName), reported);
            Assert.Equal(fileName, document.RootElement.GetProperty("fileName").GetString());
        }
        finally
        {
            if (File.Exists(savedPath))
            {
                File.Delete(savedPath);
            }
        }
    }

    [Fact]
    public async Task PatternPackList_NamesFilesRelativeToThePatternsRoot()
    {
        var response = await _fixture.Client.GetAsync("/api/PatternFiles/packs");

        Assert.True(response.IsSuccessStatusCode, $"packs returned {(int)response.StatusCode}");
        AssertSanitized(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ApiError_SerializesTheGivenSentenceAndNothingElse()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await ApiError.WriteAsync(context, 500, "Failed to save pattern file");

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal("""{"error":"Failed to save pattern file"}""", body);
    }

    [Fact]
    public void ApiError_PayloadCarriesOnlyTheErrorField()
    {
        var json = JsonSerializer.Serialize(ApiError.Payload("Failed to load pattern file"));

        Assert.Equal("""{"error":"Failed to load pattern file"}""", json);
    }

    /// <summary>
    /// A response body may not name where anything lives on this machine, and may not repeat what
    /// an exception said. The working directory is the pattern root's parent, so it stands in for
    /// every absolute path the pattern APIs could otherwise disclose.
    /// </summary>
    private static void AssertSanitized(string body)
    {
        var cwd = Directory.GetCurrentDirectory();

        Assert.DoesNotContain(cwd, body);
        Assert.DoesNotContain(JsonEncode(cwd), body);
        Assert.DoesNotContain("/home/", body);
        Assert.DoesNotContain("/etc/", body);
        Assert.DoesNotContain(@"C:\", body);
        Assert.DoesNotContain(@"C:\\", body);

        foreach (var giveaway in ExceptionGiveaways)
        {
            Assert.DoesNotContain(giveaway, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>How the value would look once serialized: on Windows every separator is escaped.</summary>
    private static string JsonEncode(string value) =>
        JsonSerializer.Serialize(value).Trim('"');

    private static string SaveRequestBody(string fileName) => $$"""
    {
      "fileName": {{JsonSerializer.Serialize(fileName)}},
      "saveToCustom": true,
      "patternFile": {
        "metadata": { "name": "Sanitize Test", "version": "1.0.0", "author": "Tester", "description": "d", "tags": [], "created": "2026-01-01T00:00:00Z", "compatibility": "1.0.0" },
        "ships": { "sidewinder": { "displayName": "Sidewinder", "class": "small", "role": "combat", "events": {} } }
      }
    }
    """;
}
