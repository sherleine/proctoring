using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Proctoring.Tests.Shared;

namespace Proctoring.IntegrationTests;

/// <summary>Small typed helper over the HTTP API for the Phase 4 tests.</summary>
public sealed class TestApi(HttpClient client, ApiFactory factory)
{
    public HttpClient Client => client;
    private DateTime Now => factory.Clock.GetUtcNow().UtcDateTime;

    public async Task<(string Id, string Token)> NewSessionAsync()
    {
        var response = await client.PostAsync("/api/v1/sessions",
            new StringContent("""{ "candidateId": "cand-55", "examId": "exam-7" }""", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return ((string)body["sessionId"]!, (string)body["sessionToken"]!);
    }

    public async Task<HttpResponseMessage> UploadAsync(string sessionId, string token, Guid eventId, byte[]? wav = null, int sequence = 0, JsonObject? metadata = null)
    {
        wav ??= TestAudio.Wav(1.5);
        var form = new MultipartFormDataContent();
        var meta = new StringContent((metadata ?? TestAudio.Metadata(wav, eventId, Now, sequence: sequence)).ToJsonString(), Encoding.UTF8);
        meta.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(meta, "metadata");
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "audio", "clip.wav");
        return await SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/events", token, form);
    }

    public async Task<JsonNode> GetEventAsync(string sessionId, string token, Guid eventId)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sessionId}/events/{eventId}", token);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    public async Task<string> StatusAsync(string sessionId, string token, Guid eventId) =>
        (string)(await GetEventAsync(sessionId, token, eventId))["status"]!;

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? token, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}

public sealed record SseMessage(string? Id, string? Event, string? Data, string? Comment)
{
    public JsonNode Json => JsonNode.Parse(Data!)!;
    public override string ToString() => Comment is not null ? $":{Comment}" : $"[{Id}] {Event}: {Data}";
}

/// <summary>
/// A Server-Sent Events client as a browser would use it (fetch + Authorization header, since EventSource cannot
/// send headers). A background pump parses the stream into messages.
/// </summary>
public sealed class SseStream : IAsyncDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly Channel<SseMessage> _messages = Channel.CreateUnbounded<SseMessage>();
    private readonly CancellationTokenSource _cts = new();
    private readonly StringBuilder _raw = new();
    private readonly Task _pump;

    private SseStream(HttpResponseMessage response)
    {
        _response = response;
        _pump = Task.Run(PumpAsync);
    }

    public HttpResponseMessage Response => _response;

    /// <summary>Everything received so far, verbatim.</summary>
    public string Raw
    {
        get { lock (_raw) return _raw.ToString(); }
    }

    public static async Task<HttpResponseMessage> RequestAsync(HttpClient client, string sessionId, string? token, string? lastEventId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/sessions/{sessionId}/updates");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (lastEventId is not null) request.Headers.Add("Last-Event-ID", lastEventId);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    public static async Task<SseStream> OpenAsync(HttpClient client, string sessionId, string token, string? lastEventId = null)
    {
        var response = await RequestAsync(client, sessionId, token, lastEventId);
        // Read the body only on failure: a successful event stream never ends.
        if (!response.IsSuccessStatusCode)
            Assert.Fail($"SSE open failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        return new SseStream(response);
    }

    /// <summary>Next message (comments included); null when the server closed the stream.</summary>
    public async Task<SseMessage?> NextAsync(double timeoutSeconds = 10)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            return await _messages.Reader.ReadAsync(timeout.Token);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"No SSE message within {timeoutSeconds} s. Received so far:\n{Raw}");
        }
    }

    /// <summary>Next non-comment message.</summary>
    public async Task<SseMessage?> NextEventAsync(double timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var left = Math.Max(0.05, (deadline - DateTime.UtcNow).TotalSeconds);
            var m = await NextAsync(left);
            if (m is null || m.Comment is null) return m;
        }
    }

    /// <summary>Reads event messages until one matches.</summary>
    public async Task<SseMessage> WaitForAsync(Func<SseMessage, bool> match, double timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var left = Math.Max(0.05, (deadline - DateTime.UtcNow).TotalSeconds);
            var m = await NextEventAsync(left) ?? throw new InvalidOperationException($"Stream closed. Received:\n{Raw}");
            if (match(m)) return m;
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await using var stream = await _response.Content.ReadAsStreamAsync(_cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? id = null, ev = null, comment = null;
            var data = new List<string>();
            while (await reader.ReadLineAsync(_cts.Token) is { } line)
            {
                lock (_raw) _raw.Append(line).Append('\n');
                if (line.Length == 0)
                {
                    if (data.Count > 0 || ev is not null || comment is not null)
                        await _messages.Writer.WriteAsync(new SseMessage(id, ev, data.Count > 0 ? string.Join("\n", data) : null, comment));
                    id = ev = comment = null;
                    data.Clear();
                    continue;
                }
                if (line.StartsWith(':')) { comment = line[1..].Trim(); continue; }
                var colon = line.IndexOf(':');
                var field = colon < 0 ? line : line[..colon];
                var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');
                switch (field)
                {
                    case "id": id = value; break;
                    case "event": ev = value; break;
                    case "data": data.Add(value); break;
                }
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            _messages.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _response.Dispose();
        // The in-memory test server may not abort a pending read on cancellation; the stream ends at latest
        // when the factory shuts the app down, so do not wait for the pump indefinitely.
        try { await _pump.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* closed or still blocked */ }
        _cts.Dispose();
    }
}
