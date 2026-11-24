using Microsoft.Extensions.Logging;
using Proctoring.Application.Events;
using StackExchange.Redis;

namespace Proctoring.Infrastructure.Redis;

/// <summary>Handles one stream entry. Must be idempotent: an entry can be delivered more than once.</summary>
public delegate Task<ProcessingOutcome> StreamEntryHandler(IReadOnlyDictionary<string, string> fields, int deliveryCount, CancellationToken ct);

/// <summary>Where a consumer reads from and where it dead-letters.</summary>
public sealed record StreamConsumerOptions(
    string Stream,
    string Group,
    string DeadLetterStream,
    string ConsumerName,
    int MaxDeliveries,
    int ClaimIdleMs,
    int ClaimIntervalMs,
    int ReadBatch,
    int StreamMaxLength);

/// <summary>
/// One consumer of a Redis consumer group, driven by <see cref="RunOnceAsync"/> (a hosted service loops it).
///
///  * New entries: XREADGROUP &gt;. Several instances with distinct consumer names share the work.
///  * Crash recovery: entries left pending by a consumer that died (idle &gt; ClaimIdleMs) are taken over with
///    XAUTOCLAIM. Our own Retry entries come back the same way, which spaces out retries.
///  * Acknowledge (XACK) only after the handler returned Applied/Ignored, i.e. after the state change was saved.
///  * Retry: not acknowledged; redelivered after ClaimIdleMs.
///  * DeadLetter, or delivered more than MaxDeliveries times: XADD to the dead-letter stream (original fields +
///    dlq.* fields), then XACK. If the dead-letter write fails, nothing is acknowledged and it is tried again.
///  * The group is (re)created from the first entry when missing, e.g. after Redis lost its data.
/// </summary>
public sealed class RedisStreamConsumer(RedisConnection redis, StreamConsumerOptions options, StreamEntryHandler handler, TimeProvider clock, ILogger logger)
{
    private bool _groupEnsured;
    private DateTimeOffset _nextClaim = DateTimeOffset.MinValue;
    private DateTimeOffset _nextCleanup = DateTimeOffset.MinValue;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan IdleConsumerAge = TimeSpan.FromHours(24);

    public StreamConsumerOptions Options => options;

    /// <summary>Processes at most one batch of reclaimed entries and one batch of new entries. Returns how many
    /// entries were handled (0 = nothing to do).</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var db = await redis.GetDatabaseAsync();
        try
        {
            if (!_groupEnsured)
            {
                await RedisConnection.EnsureGroupAsync(db, options.Stream, options.Group);
                _groupEnsured = true;
            }

            var handled = 0;
            var now = clock.GetUtcNow();
            if (now >= _nextClaim)
            {
                _nextClaim = now.AddMilliseconds(options.ClaimIntervalMs);
                handled += await ClaimStaleAsync(db, ct);
            }
            if (now >= _nextCleanup)
            {
                _nextCleanup = now + CleanupInterval;
                await RemoveIdleConsumersAsync(db);
            }

            var entries = await db.StreamReadGroupAsync(options.Stream, options.Group, options.ConsumerName, StreamPosition.NewMessages, options.ReadBatch);
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.IsNull) continue;
                await HandleAsync(db, entry, deliveryCount: 1, ct);
                handled++;
            }
            return handled;
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("NOGROUP", StringComparison.Ordinal))
        {
            // Stream or group vanished (Redis restarted without persistence, or someone deleted it).
            logger.LogWarning("Consumer group {Group} on {Stream} is missing; recreating it", options.Group, options.Stream);
            _groupEnsured = false;
            return 0;
        }
    }

    private async Task<int> ClaimStaleAsync(IDatabase db, CancellationToken ct)
    {
        var handled = 0;
        RedisValue start = "0-0";
        do
        {
            var claimed = await db.StreamAutoClaimAsync(options.Stream, options.Group, options.ConsumerName, options.ClaimIdleMs, start, options.ReadBatch);
            foreach (var entry in claimed.ClaimedEntries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.IsNull) continue;
                var count = await DeliveryCountAsync(db, entry.Id);
                logger.LogInformation("Reclaimed {EntryId} from {Stream} (delivery {Count})", (string?)entry.Id, options.Stream, count);
                await HandleAsync(db, entry, count, ct);
                handled++;
            }
            // Entries trimmed from the stream while pending are dropped from the PEL by Redis 7 itself.
            start = claimed.NextStartId;
        } while (start != "0-0" && handled < options.ReadBatch * 10);
        return handled;
    }

    private async Task<int> DeliveryCountAsync(IDatabase db, RedisValue id)
    {
        var pending = await db.StreamPendingMessagesAsync(options.Stream, options.Group, 1, options.ConsumerName, id, id);
        return pending.Length == 1 ? pending[0].DeliveryCount : 1;
    }

    private async Task HandleAsync(IDatabase db, StreamEntry entry, int deliveryCount, CancellationToken ct)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in entry.Values) fields[v.Name!] = v.Value.ToString();

        if (deliveryCount > options.MaxDeliveries)
        {
            await DeadLetterAsync(db, entry, fields, $"delivered {deliveryCount} times (limit {options.MaxDeliveries})", deliveryCount);
            return;
        }

        ProcessingOutcome outcome;
        try
        {
            outcome = await handler(fields, deliveryCount, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Handler failed for {EntryId} on {Stream}; it will be redelivered", (string?)entry.Id, options.Stream);
            outcome = ProcessingOutcome.Retry(ex.GetType().Name);
        }

        switch (outcome.Disposition)
        {
            case ProcessingDisposition.Applied:
            case ProcessingDisposition.Ignored:
                await db.StreamAcknowledgeAsync(options.Stream, options.Group, entry.Id);
                break;
            case ProcessingDisposition.DeadLetter:
                await DeadLetterAsync(db, entry, fields, outcome.Reason ?? "rejected", deliveryCount);
                break;
            case ProcessingDisposition.Retry:
                logger.LogInformation("{EntryId} on {Stream} left pending for retry: {Reason}", (string?)entry.Id, options.Stream, outcome.Reason);
                break;
        }
    }

    private async Task DeadLetterAsync(IDatabase db, StreamEntry entry, Dictionary<string, string> fields, string reason, int deliveryCount)
    {
        if (reason.Length > 500) reason = reason[..500];
        var dlq = fields
            .Where(f => !f.Key.StartsWith("dlq.", StringComparison.Ordinal))
            .Select(f => new NameValueEntry(f.Key, f.Value))
            .Concat([
                new NameValueEntry("dlq.reason", reason),
                new NameValueEntry("dlq.deliveryCount", deliveryCount),
                new NameValueEntry("dlq.originalStream", options.Stream),
                new NameValueEntry("dlq.originalId", entry.Id),
                new NameValueEntry("dlq.consumerGroup", options.Group),
                new NameValueEntry("dlq.failedAt", clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")),
            ])
            .ToArray();
        int? maxLength = options.StreamMaxLength > 0 ? options.StreamMaxLength : null;
        // If this throws, the entry stays pending (not acknowledged) and is dead-lettered on a later delivery.
        await db.StreamAddAsync(options.DeadLetterStream, dlq, maxLength: maxLength, useApproximateMaxLength: true);
        await db.StreamAcknowledgeAsync(options.Stream, options.Group, entry.Id);
        logger.LogWarning("{EntryId} on {Stream} moved to {DeadLetterStream}: {Reason}", (string?)entry.Id, options.Stream, options.DeadLetterStream, reason);
    }

    /// <summary>Every restart uses a new consumer name; forget old names that have nothing pending.</summary>
    private async Task RemoveIdleConsumersAsync(IDatabase db)
    {
        var consumers = await db.StreamConsumerInfoAsync(options.Stream, options.Group);
        foreach (var c in consumers)
        {
            if (c.Name == options.ConsumerName || c.PendingMessageCount > 0 || c.IdleTimeInMilliseconds < IdleConsumerAge.TotalMilliseconds) continue;
            await db.StreamDeleteConsumerAsync(options.Stream, options.Group, c.Name);
        }
    }

    public static string NewConsumerName() =>
        $"{Environment.MachineName}-{Environment.ProcessId}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";
}
