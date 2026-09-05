using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace EDButtkicker.Hosting;

/// <summary>How a request body ended up being read.</summary>
public enum BoundedBodyStatus
{
    /// <summary>A body was read whole and is within the cap.</summary>
    Ok,

    /// <summary>There was no body, or it was whitespace only.</summary>
    Empty,

    /// <summary>The body is larger than <see cref="RequestLimits.MaxRequestBodyBytes"/>.</summary>
    TooLarge
}

/// <summary>The outcome of a bounded read. <see cref="Text"/> is empty unless the status is Ok.</summary>
public readonly record struct BoundedBody(BoundedBodyStatus Status, string Text);

/// <summary>
/// Reads request bodies with a hard byte cap. Content-Length is a hint that can be missing or wrong,
/// so the cap is enforced against the bytes actually read: a body that grows past the limit is
/// abandoned rather than buffered, and the caller answers 413.
/// </summary>
public static class BoundedRequestReader
{
    private const int ChunkSize = 8192;

    /// <summary>Reads the body, stopping as soon as it is known to be over the cap.</summary>
    public static async Task<BoundedBody> ReadAsync(
        HttpContext context,
        long limitBytes = RequestLimits.MaxRequestBodyBytes)
    {
        // A declared length over the cap is refused without reading a byte; the loop below is what
        // actually enforces the limit, because the declaration is the caller's word for it.
        if (context.Request.ContentLength > limitBytes)
        {
            return new BoundedBody(BoundedBodyStatus.TooLarge, string.Empty);
        }

        var buffer = new byte[ChunkSize];
        using var accumulated = new MemoryStream();

        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(), context.RequestAborted);
            if (read == 0)
            {
                break;
            }

            if (accumulated.Length + read > limitBytes)
            {
                return new BoundedBody(BoundedBodyStatus.TooLarge, string.Empty);
            }

            accumulated.Write(buffer, 0, read);
        }

        var text = Encoding.UTF8.GetString(accumulated.GetBuffer(), 0, (int)accumulated.Length);

        return string.IsNullOrWhiteSpace(text)
            ? new BoundedBody(BoundedBodyStatus.Empty, string.Empty)
            : new BoundedBody(BoundedBodyStatus.Ok, text);
    }

    /// <summary>
    /// The body text, or <c>null</c> when this method has already written the response: 413 for a
    /// body over the cap, and 400 with <paramref name="emptyBodyError"/> for a missing body. Pass a
    /// null <paramref name="emptyBodyError"/> where an empty body is allowed; the result is then an
    /// empty string rather than a rejection.
    /// </summary>
    public static async Task<string?> ReadOrRespondAsync(HttpContext context, string? emptyBodyError)
    {
        var body = await ReadAsync(context);

        switch (body.Status)
        {
            case BoundedBodyStatus.TooLarge:
                await WriteTooLargeAsync(context);
                return null;

            case BoundedBodyStatus.Empty when emptyBodyError != null:
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, emptyBodyError);
                return null;

            case BoundedBodyStatus.Empty:
                return string.Empty;

            default:
                return body.Text;
        }
    }

    /// <summary>
    /// Deserializes request JSON with the depth cap applied. Malformed JSON, JSON nested deeper than
    /// <see cref="RequestLimits.MaxJsonDepth"/> and values of the wrong shape all come back false;
    /// no exception detail reaches the caller.
    /// </summary>
    public static bool TryDeserialize<T>(string json, out T? value, JsonSerializerOptions? options = null)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json, options ?? RequestLimits.Json);
            return value != null;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    /// <summary>As <see cref="TryDeserialize{T}"/>, for handlers that walk the document themselves.</summary>
    public static bool TryParseDocument(string json, out JsonElement root)
    {
        try
        {
            using var document = JsonDocument.Parse(json, RequestLimits.Document);
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            root = default;
            return false;
        }
    }

    /// <summary>
    /// Copies at most <paramref name="limitBytes"/> from <paramref name="source"/>. False means the
    /// source had more to give, so a lying Content-Length cannot fill the disk.
    /// </summary>
    public static async Task<bool> CopyBoundedAsync(
        Stream source,
        Stream destination,
        long limitBytes,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[ChunkSize];
        long copied = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return true;
            }

            copied += read;
            if (copied > limitBytes)
            {
                return false;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    /// <summary>413 with the stable body-too-large error.</summary>
    public static Task WriteTooLargeAsync(HttpContext context) =>
        WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, RequestLimits.BodyTooLargeError);

    /// <summary>An error response in the shape the rest of the API uses.</summary>
    public static Task WriteErrorAsync(HttpContext context, int statusCode, string error)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        return context.Response.WriteAsync(JsonSerializer.Serialize(new { error }));
    }
}
