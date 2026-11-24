using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Proctoring.Application.Errors;
using Proctoring.Application.Events;
using Proctoring.Application.Sessions;
using Proctoring.Api.Errors;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Proctoring.Api.Endpoints;

/// <summary>
/// GET /api/v1/sessions/{sessionId}/updates - Server-Sent Events with the session's event changes.
///
/// SQLite stays the source of truth: each SSE message is read from the event_updates change log (written in
/// the same transaction as the state change) and carries the event's current state, exactly as
/// GET /events/{eventId} returns it. The SSE id is the change-log id, so a reconnect with Last-Event-ID resumes
/// without gaps. Redis pub/sub / in-process signals only wake the loop early; it also polls. No database
/// connection is held between polls (a new DI scope per poll).
/// </summary>
public static partial class UpdateEndpoints
{
    public const string ContentType = "text/event-stream";
    private const int ClientRetryMs = 3000;

    [GeneratedRegex("^ses_[A-Za-z0-9]{20,}$")]
    private static partial Regex SessionIdPattern();

    public static void MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/sessions/{sessionId}/updates", StreamAsync)
            .WithTags("Events")
            .WithName("StreamEventUpdates")
            .WithSummary("Server-Sent Events: event status changes of the session (resume with Last-Event-ID)")
            .Produces(StatusCodes.Status200OK, contentType: ContentType)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task StreamAsync(string sessionId, HttpContext http, IServiceScopeFactory scopes,
        IEventUpdateNotifier notifier, IOptions<LiveUpdateOptions> options, IOptions<JsonOptions> json,
        TimeProvider clock, ILoggerFactory loggers, IHostApplicationLifetime lifetime)
    {
        // Ends when the client disconnects or the application shuts down (open streams must not block shutdown;
        // clients reconnect to another instance with Last-Event-ID).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, lifetime.ApplicationStopping);
        var ct = linked.Token;
        // Authentication happens before anything is written, so failures are ordinary problem+json responses.
        var token = BearerToken.Read(http);
        if (!SessionIdPattern().IsMatch(sessionId))
        {
            await Problems.NotFound(http).ExecuteAsync(http);
            return;
        }
        long lastId;
        bool resync;
        await using (var scope = scopes.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SessionService>().AuthorizeAsync(sessionId, token, ct);
            var updates = scope.ServiceProvider.GetRequiredService<IEventUpdateRepository>();
            (lastId, resync) = await ResolveStartAsync(http, sessionId, updates, ct);
        }

        var logger = loggers.CreateLogger("Proctoring.Api.Updates");
        var o = options.Value;
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = ContentType;
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no"; // nginx: do not buffer the stream
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var writer = new SseWriter(http.Response, json.Value.SerializerOptions);
        try
        {
            await writer.WriteRawAsync($"retry: {ClientRetryMs}\n\n", ct);
            // "ready" tells the client where the stream starts. After "resync" the client reloads
            // GET /events (state it may have missed) - the stream itself continues without a gap.
            await writer.WriteEventAsync(resync ? "resync" : "ready", lastId, new { sessionId, lastEventId = lastId.ToString() }, ct);

            var lastWrite = clock.GetUtcNow();
            var lastAuth = lastWrite;
            while (!ct.IsCancellationRequested)
            {
                var sent = 0;
                var full = false;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    var now = clock.GetUtcNow();
                    if (now - lastAuth >= TimeSpan.FromSeconds(o.ReauthorizeSeconds))
                    {
                        lastAuth = now;
                        try
                        {
                            await scope.ServiceProvider.GetRequiredService<SessionService>().AuthorizeAsync(sessionId, token, ct);
                        }
                        catch (Exception ex) when (ex is TokenExpiredException or SessionNotFoundException or InvalidCredentialsException)
                        {
                            await writer.WriteEventAsync("end", null, new { reason = ex is TokenExpiredException ? "token-expired" : "unauthorized" }, ct);
                            return;
                        }
                    }

                    var repo = scope.ServiceProvider.GetRequiredService<IEventUpdateRepository>();
                    var updates = await repo.GetAfterAsync(sessionId, lastId, o.BatchSize, ct);
                    full = updates.Count == o.BatchSize;
                    if (updates.Count > 0)
                    {
                        // Several changes of one event in a batch are sent once, at the position of its last change,
                        // with its current state; ids stay strictly increasing.
                        var lastPerEvent = updates.GroupBy(u => u.EventId).Select(g => g.Last()).OrderBy(u => u.Id).ToList();
                        var current = await repo.GetEventsAsync(sessionId, lastPerEvent.Select(u => u.EventId).ToList(), ct);
                        foreach (var u in lastPerEvent)
                        {
                            if (current.TryGetValue(u.EventId, out var e))
                                await writer.WriteEventAsync("event.updated", u.Id, EventMapper.ToResponse(e), ct);
                            sent++;
                        }
                        lastId = updates[^1].Id;
                    }
                }
                if (sent > 0) lastWrite = clock.GetUtcNow();
                if (full) continue; // more pending: read the next batch right away

                // Wait for a signal or the poll interval, outside any DB scope.
                await notifier.WaitAsync(sessionId, TimeSpan.FromMilliseconds(o.PollIntervalMs), ct);
                if (clock.GetUtcNow() - lastWrite >= TimeSpan.FromSeconds(o.HeartbeatSeconds))
                {
                    await writer.WriteRawAsync(": heartbeat\n\n", ct);
                    lastWrite = clock.GetUtcNow();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Connection closed while writing.
        }
        catch (Exception ex)
        {
            // Headers are already sent: end the stream; the client reconnects with Last-Event-ID.
            logger.LogWarning("Update stream for session {SessionId} ended after an error ({Error})", sessionId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// No Last-Event-ID: start after the newest entry (the client loads GET /events once "ready" arrives).
    /// A Last-Event-ID that is not a number, or older than what is still retained: "resync".
    /// </summary>
    private static async Task<(long LastId, bool Resync)> ResolveStartAsync(HttpContext http, string sessionId, IEventUpdateRepository updates, CancellationToken ct)
    {
        var header = http.Request.Headers["Last-Event-ID"].ToString();
        if (string.IsNullOrWhiteSpace(header)) return (await updates.GetLatestIdAsync(sessionId, ct), false);
        if (!long.TryParse(header.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var lastId))
            return (await updates.GetLatestIdAsync(sessionId, ct), true);
        var oldest = await updates.GetOldestRetainedIdAsync(ct);
        // Ids in (lastId, oldest) were pruned and may have belonged to this session.
        if (oldest > 0 && lastId < oldest - 1) return (await updates.GetLatestIdAsync(sessionId, ct), true);
        return (lastId, false);
    }

    private sealed class SseWriter(HttpResponse response, JsonSerializerOptions json)
    {
        public async Task WriteEventAsync(string name, long? id, object data, CancellationToken ct)
        {
            var sb = new StringBuilder();
            if (id is not null) sb.Append("id: ").Append(id.Value).Append('\n');
            sb.Append("event: ").Append(name).Append('\n');
            // Serialized JSON contains no raw newlines, so a single data line is enough.
            sb.Append("data: ").Append(JsonSerializer.Serialize(data, data.GetType(), json)).Append("\n\n");
            await WriteRawAsync(sb.ToString(), ct);
        }

        public async Task WriteRawAsync(string text, CancellationToken ct)
        {
            await response.WriteAsync(text, ct);
            await response.Body.FlushAsync(ct);
        }
    }
}
