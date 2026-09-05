using System.Net;
using System.Text;
using EDButtkicker.Hosting;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// What the loopback API accepts from a caller who sends too much: a body over the byte cap is
/// refused with 413 by the handler itself - never buffered whole - and JSON nested past the depth
/// cap is a bad request rather than a crash. The bound is enforced in the handler, not only by
/// Kestrel, so it still holds here where TestServer is the transport.
/// </summary>
public class RequestBodyAndImportLimitTests : IClassFixture<WebUiTestServerFixture>
{
    private readonly WebUiTestServerFixture _fixture;

    public RequestBodyAndImportLimitTests(WebUiTestServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task JournalPath_WithABodyJustUnderTheCap_IsNotRefusedForSize()
    {
        // A valid JSON object of very nearly the largest allowed body: whatever the API makes of the
        // path itself, the size is not what it objects to.
        var padding = new string('p', (int)RequestLimits.MaxRequestBodyBytes - 100);
        var body = $"{{\"path\":\"{padding}\"}}";

        var response = await _fixture.Client.PostAsync("/api/journal/path", JsonContent(body));

        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task JournalPath_WithABodyOverTheCap_Is413()
    {
        var oversized = new byte[RequestLimits.MaxRequestBodyBytes + 1024];
        Array.Fill(oversized, (byte)'x');

        var response = await _fixture.Client.PostAsync("/api/journal/path", JsonContent(oversized));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains(RequestLimits.BodyTooLargeError, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ValidatePattern_WithJsonNestedPastTheDepthCap_Is400NotAnUnhandledError()
    {
        var depth = RequestLimits.MaxJsonDepth + 8;
        var body = new string('{', depth) + "\"a\":1" + new string('}', depth);

        var response = await _fixture.Client.PostAsync("/api/PatternEditor/validate", JsonContent(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static ByteArrayContent JsonContent(string body) => JsonContent(Encoding.UTF8.GetBytes(body));

    private static ByteArrayContent JsonContent(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        return content;
    }
}
