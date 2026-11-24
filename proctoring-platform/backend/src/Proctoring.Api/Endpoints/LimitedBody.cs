namespace Proctoring.Api.Endpoints;

/// <summary>
/// Reads a request body with a hard size limit. Checking Content-Length alone is not enough: a client can
/// send the body chunked (no Content-Length) and an unlimited read would buffer all of it in memory.
/// </summary>
public static class LimitedBody
{
    /// <summary>The body, or null when it is (or declares to be) larger than <paramref name="maxBytes"/>.</summary>
    public static async Task<byte[]?> ReadAsync(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        if (request.ContentLength > maxBytes) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null; // stop reading as soon as the limit is passed
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
