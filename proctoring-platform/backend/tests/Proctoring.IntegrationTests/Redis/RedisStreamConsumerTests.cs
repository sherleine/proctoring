using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Proctoring.Application.Events;
using Proctoring.Infrastructure.Redis;
using StackExchange.Redis;

namespace Proctoring.IntegrationTests.Redis;

/// <summary>The consumer-group engine against a real Redis: groups, acks, retries, crash recovery, dead letters.</summary>
[Collection("redis")]
public sealed class RedisStreamConsumerTests(RedisFixture redis) : IAsyncLifetime
{
    private readonly RedisNames _n = RedisNames.New();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private RedisConnection _connection = null!;

    public Task InitializeAsync()
    {
        _connection = _n.Connection(redis.Endpoint);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private RedisStreamConsumer Consumer(StreamEntryHandler handler, string name = "c1", int maxDeliveries = 3) =>
        new(_connection, new StreamConsumerOptions(_n.Results, _n.BackendGroup, _n.ResultsDlq, name, maxDeliveries,
            ClaimIdleMs: 1000, ClaimIntervalMs: 100, ReadBatch: 10, StreamMaxLength: 1000), handler, _clock, NullLogger.Instance);

    private Task<RedisValue> AddAsync(int n) =>
        redis.Db.StreamAddAsync(_n.Results, [new NameValueEntry("n", n), new NameValueEntry("messageType", "test")]);

    private async Task<long> PendingAsync() => (await redis.Db.StreamPendingAsync(_n.Results, _n.BackendGroup)).PendingMessageCount;

    /// <summary>Lets the pending entries become "idle" for XAUTOCLAIM and the claim interval elapse.</summary>
    private async Task IdleAsync()
    {
        await Task.Delay(1100);
        _clock.Advance(TimeSpan.FromSeconds(2));
    }

    private static StreamEntryHandler Returns(ProcessingOutcome outcome, ConcurrentBag<(string N, int Delivery)>? seen = null) =>
        (fields, delivery, _) =>
        {
            seen?.Add((fields["n"], delivery));
            return Task.FromResult(outcome);
        };

    [Fact]
    public async Task Group_is_created_from_the_first_entry_and_creation_is_idempotent()
    {
        await AddAsync(1); // published before any consumer existed
        await AddAsync(2);
        var db = await _connection.GetDatabaseAsync();
        await RedisConnection.EnsureGroupAsync(db, _n.Results, _n.BackendGroup);
        await RedisConnection.EnsureGroupAsync(db, _n.Results, _n.BackendGroup); // BUSYGROUP is fine

        var seen = new ConcurrentBag<(string, int)>();
        Assert.Equal(2, await Consumer(Returns(ProcessingOutcome.Applied, seen)).RunOnceAsync(default));
        Assert.Equal(["1", "2"], seen.Select(s => s.Item1).Order());
        Assert.Equal(0, await PendingAsync());
    }

    [Fact]
    public async Task Applied_and_ignored_entries_are_acknowledged()
    {
        var consumer = Consumer((f, _, _) => Task.FromResult(f["n"] == "1" ? ProcessingOutcome.Applied : ProcessingOutcome.Ignored("dup")));
        await consumer.RunOnceAsync(default); // creates the group
        await AddAsync(1);
        await AddAsync(2);
        Assert.Equal(2, await consumer.RunOnceAsync(default));
        Assert.Equal(0, await PendingAsync());
        Assert.Equal(0, await consumer.RunOnceAsync(default));
    }

    [Fact]
    public async Task Retry_leaves_the_entry_pending_and_it_is_dead_lettered_after_the_delivery_limit()
    {
        var seen = new ConcurrentBag<(string N, int Delivery)>();
        var consumer = Consumer(Returns(ProcessingOutcome.Retry("db down"), seen), maxDeliveries: 2);
        await consumer.RunOnceAsync(default);
        var id = await AddAsync(7);

        await consumer.RunOnceAsync(default);          // delivery 1 -> retry
        Assert.Equal(1, await PendingAsync());
        await IdleAsync();
        await consumer.RunOnceAsync(default);          // reclaimed, delivery 2 -> retry
        Assert.Equal(1, await PendingAsync());
        await IdleAsync();
        await consumer.RunOnceAsync(default);          // delivery 3 > limit -> dead letter

        Assert.Equal([1, 2], seen.Select(s => s.Delivery).Order());
        Assert.Equal(0, await PendingAsync());
        var dlq = await redis.Db.StreamRangeAsync(_n.ResultsDlq);
        var entry = Assert.Single(dlq);
        var fields = entry.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());
        Assert.Equal("7", fields["n"]);                       // original fields kept
        Assert.Equal("test", fields["messageType"]);
        Assert.Equal(id.ToString(), fields["dlq.originalId"]);
        Assert.Equal(_n.Results, fields["dlq.originalStream"]);
        Assert.Equal("3", fields["dlq.deliveryCount"]);
        Assert.Contains("delivered 3 times", fields["dlq.reason"]);
    }

    [Fact]
    public async Task DeadLetter_outcome_moves_the_entry_to_the_dead_letter_stream_at_once()
    {
        var consumer = Consumer(Returns(ProcessingOutcome.DeadLetter("payload violates schema")));
        await consumer.RunOnceAsync(default);
        await AddAsync(1);
        await consumer.RunOnceAsync(default);
        Assert.Equal(0, await PendingAsync());
        var entry = Assert.Single(await redis.Db.StreamRangeAsync(_n.ResultsDlq));
        Assert.Equal("payload violates schema", entry["dlq.reason"].ToString());
    }

    [Fact]
    public async Task Handler_exception_is_treated_as_retry()
    {
        var consumer = Consumer((_, _, _) => throw new InvalidOperationException("boom"));
        await consumer.RunOnceAsync(default);
        await AddAsync(1);
        await consumer.RunOnceAsync(default);
        Assert.Equal(1, await PendingAsync());
        Assert.Empty(await redis.Db.StreamRangeAsync(_n.ResultsDlq));
    }

    [Fact]
    public async Task Entries_of_a_crashed_consumer_are_reclaimed_by_another_consumer()
    {
        var survivorSeen = new ConcurrentBag<(string N, int Delivery)>();
        var survivor = Consumer(Returns(ProcessingOutcome.Applied, survivorSeen), name: "survivor");
        await survivor.RunOnceAsync(default);
        await AddAsync(1);
        // "crashed" reads the entry and dies before acknowledging it.
        var read = await redis.Db.StreamReadGroupAsync(_n.Results, _n.BackendGroup, "crashed", StreamPosition.NewMessages, 10);
        Assert.Single(read);

        Assert.Equal(0, await survivor.RunOnceAsync(default)); // not idle long enough yet
        await IdleAsync();
        Assert.Equal(1, await survivor.RunOnceAsync(default));
        Assert.Equal(("1", 2), Assert.Single(survivorSeen));   // second delivery
        Assert.Equal(0, await PendingAsync());
    }

    [Fact]
    public async Task Several_consumers_share_the_work_without_processing_an_entry_twice()
    {
        var seen = new ConcurrentBag<(string Consumer, string N)>();
        StreamEntryHandler For(string name) => (f, _, _) =>
        {
            seen.Add((name, f["n"]));
            return Task.FromResult(ProcessingOutcome.Applied);
        };
        var a = Consumer(For("a"), "a");
        var b = Consumer(For("b"), "b");
        await a.RunOnceAsync(default);
        for (var i = 0; i < 40; i++) await AddAsync(i);

        async Task Drain(RedisStreamConsumer c)
        {
            while (await c.RunOnceAsync(default) > 0) { }
        }
        await Task.WhenAll(Drain(a), Drain(b));
        await Drain(a);

        Assert.Equal(40, seen.Count);
        Assert.Equal(40, seen.Select(s => s.N).Distinct().Count());
        Assert.Equal(0, await PendingAsync());
    }

    [Fact]
    public async Task Failed_dead_letter_write_keeps_the_entry_pending_until_it_succeeds()
    {
        var consumer = Consumer(Returns(ProcessingOutcome.DeadLetter("invalid")));
        await consumer.RunOnceAsync(default);
        await redis.Db.StringSetAsync(_n.ResultsDlq, "not a stream"); // XADD to it fails with WRONGTYPE
        await AddAsync(1);

        await Assert.ThrowsAnyAsync<RedisException>(() => consumer.RunOnceAsync(default));
        Assert.Equal(1, await PendingAsync()); // not acknowledged, not lost

        await redis.Db.KeyDeleteAsync(_n.ResultsDlq);
        await IdleAsync();
        await consumer.RunOnceAsync(default);
        Assert.Equal(0, await PendingAsync());
        Assert.Single(await redis.Db.StreamRangeAsync(_n.ResultsDlq));
    }

    [Fact]
    public async Task Missing_group_is_recreated_after_the_stream_disappears()
    {
        var seen = new ConcurrentBag<(string, int)>();
        var consumer = Consumer(Returns(ProcessingOutcome.Applied, seen));
        await consumer.RunOnceAsync(default);
        await redis.Db.KeyDeleteAsync(_n.Results); // e.g. Redis restarted without persistence

        Assert.Equal(0, await consumer.RunOnceAsync(default)); // NOGROUP handled
        await AddAsync(5);
        var handled = 0;
        for (var i = 0; i < 3 && handled == 0; i++) handled += await consumer.RunOnceAsync(default);
        Assert.Equal(1, handled);
        Assert.Equal("5", Assert.Single(seen).Item1);
    }
}
