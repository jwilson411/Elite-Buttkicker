using System.Net;
using System.Reflection;
using System.Text;
using EDButtkicker.Controllers;
using EDButtkicker.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The pattern file APIs take a caller-supplied name straight from a request. These pin that a
/// traversal name is refused before it ever reaches the filesystem, on the same pipeline Program
/// runs. Nothing here touches audio hardware or binds a port.
/// </summary>
public class PatternPathTraversalApiTests : IClassFixture<WebUiTestServerFixture>
{
    private const string ValidPatternJson = """
    {
      "metadata": { "name": "Guard Test", "version": "1.0.0", "author": "Tester", "description": "d", "tags": [], "created": "2026-01-01T00:00:00Z", "compatibility": "1.0.0" },
      "ships": { "sidewinder": { "displayName": "Sidewinder", "class": "small", "role": "combat", "events": {} } }
    }
    """;

    private readonly WebUiTestServerFixture _fixture;

    public PatternPathTraversalApiTests(WebUiTestServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("..%2fescape.json")]
    [InlineData("%2e%2e%2fescape.json")]
    [InlineData("/etc/passwd")]
    [InlineData("..\\escape.json")]
    [InlineData("C:\\Windows\\win.ini")]
    public async Task SavePattern_WithTraversalFileName_IsRejected(string fileName)
    {
        var body = SaveRequestBody(fileName);

        var response = await _fixture.Client.PostAsync(
            "/api/PatternEditor/save", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "patterns", "escape.json")));
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "escape.json")));
    }

    [Theory]
    [InlineData("..%2f..%2fetc%2fpasswd")]
    [InlineData("%2e%2e%2fsecret.json")]
    [InlineData("%2e%2e%5csecret.json")]
    [InlineData("%252e%252e%252fsecret.json")]
    public async Task LoadPatternForEditing_WithTraversalFileName_IsRejected(string fileName)
    {
        var response = await _fixture.Client.GetAsync($"/api/PatternEditor/load/{fileName}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SaveThenLoad_WithASanitizedName_RoundTrips()
    {
        var fileName = "edbk-guard-roundtrip.json";
        var savedPath = Path.Combine(Directory.GetCurrentDirectory(), "patterns", "Custom", fileName);
        var body = SaveRequestBody(fileName);

        try
        {
            var save = await _fixture.Client.PostAsync(
                "/api/PatternEditor/save", new StringContent(body, Encoding.UTF8, "application/json"));

            Assert.True(save.IsSuccessStatusCode, $"save returned {(int)save.StatusCode}: {await save.Content.ReadAsStringAsync()}");
            Assert.True(File.Exists(savedPath));

            var load = await _fixture.Client.GetAsync($"/api/PatternEditor/load/{fileName}");

            Assert.True(load.IsSuccessStatusCode, $"load returned {(int)load.StatusCode}: {await load.Content.ReadAsStringAsync()}");
            Assert.Contains("Guard Test", await load.Content.ReadAsStringAsync());
        }
        finally
        {
            if (File.Exists(savedPath))
            {
                File.Delete(savedPath);
            }
        }
    }

    [Theory]
    [InlineData("../x.json")]
    [InlineData("..\\x.json")]
    [InlineData("%2e%2e%2fx.json")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("exports/../../x.json")]
    public void DownloadPatternFile_WithTraversalFileName_IsRejected(string fileName)
    {
        var controller = _fixture.Services.GetRequiredService<PatternFilesController>();

        var result = controller.DownloadPatternFile(fileName);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData("../x.json")]
    [InlineData("..\\x.json")]
    [InlineData("%2e%2e%2fx.json")]
    [InlineData("/etc/passwd")]
    [InlineData("imports/../../x.json")]
    public void DeletePatternFile_WithTraversalFileName_IsRejected(string fileName)
    {
        var controller = _fixture.Services.GetRequiredService<PatternFilesController>();

        var result = controller.DeletePatternFile(fileName);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void DeletePatternFile_RemovesAnImportedFileButNotASystemFile()
    {
        var controller = _fixture.Services.GetRequiredService<PatternFilesController>();
        var patternsRoot = Path.Combine(Directory.GetCurrentDirectory(), "patterns");
        var importPath = Path.Combine(patternsRoot, "imports", "edbk-guard-delete.json");
        var systemPath = Path.Combine(patternsRoot, "Community", "edbk-guard-system.json");

        Directory.CreateDirectory(Path.GetDirectoryName(importPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(systemPath)!);
        File.WriteAllText(importPath, ValidPatternJson);
        File.WriteAllText(systemPath, ValidPatternJson);

        try
        {
            Assert.IsType<OkObjectResult>(controller.DeletePatternFile("edbk-guard-delete.json"));
            Assert.False(File.Exists(importPath));

            Assert.IsType<BadRequestObjectResult>(controller.DeletePatternFile("edbk-guard-system.json"));
            Assert.True(File.Exists(systemPath));

            Assert.IsType<NotFoundObjectResult>(controller.DeletePatternFile("edbk-guard-missing.json"));
        }
        finally
        {
            foreach (var path in new[] { importPath, systemPath })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task ImportPatternFileAsync_RejectsATraversalTargetName()
    {
        var service = _fixture.Services.GetRequiredService<PatternFileService>();
        var patternsPath = PatternsPathOf(service);
        var sourcePath = Path.Combine(Path.GetTempPath(), $"edbk-guard-source-{Guid.NewGuid():N}.json");
        var escapedPath = Path.GetFullPath(Path.Combine(patternsPath, "imports", "..", "..", "edbk-guard-escaped.json"));
        var importedPath = Path.Combine(patternsPath, "imports", "edbk-guard-import.json");

        await File.WriteAllTextAsync(sourcePath, ValidPatternJson);

        try
        {
            Assert.False(await service.ImportPatternFileAsync(sourcePath, "../../edbk-guard-escaped.json"));
            Assert.False(await service.ImportPatternFileAsync(sourcePath, "%2e%2e%2fedbk-guard-escaped.json"));
            Assert.False(File.Exists(escapedPath));

            // The same source under a plain name still imports, so the rejection is about the name.
            Assert.True(await service.ImportPatternFileAsync(sourcePath, "edbk-guard-import.json"));
            Assert.True(File.Exists(importedPath));
        }
        finally
        {
            foreach (var path in new[] { sourcePath, importedPath, escapedPath })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    private static string SaveRequestBody(string fileName) =>
        "{\"patternFile\":{\"metadata\":{\"name\":\"Guard Test\",\"author\":\"Tester\"}},\"fileName\":\"" +
        fileName.Replace("\\", "\\\\") +
        "\",\"saveToCustom\":true}";

    private static string PatternsPathOf(PatternFileService service)
    {
        var field = typeof(PatternFileService)
            .GetField("_patternsPath", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);

        return (string)field!.GetValue(service)!;
    }
}
