using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Proctoring.Application.Errors;
using Proctoring.Application.Sessions;
using Proctoring.Contracts.Sessions;
using Proctoring.Domain.Sessions;
using Proctoring.Infrastructure.Persistence;
using Proctoring.Infrastructure.Security;

namespace Proctoring.UnitTests;

/// <summary>
/// Session use cases against a real SQLite database file (created through the EF Core migrations),
/// so persistence, unique indexes and UTC round-tripping are exercised too.
/// </summary>
public sealed class SessionServiceTests : IDisposable
{
    private const string Key = "8c1f0b6e-2d4a-4f7b-9a3c-5e6d7f8a9b0c"; // UUID v4: 36 characters
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"proctoring-unit-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero));
    private readonly SessionOptions _options = new() { MaxDurationHours = 6, TokenTtlAfterEndHours = 24, IdempotencyWindowMinutes = 60 };
    private SessionTokenSecret _secret = SessionTokenServiceTests.NewSecret();
    private static readonly CreateSessionRequest Request = new("cand-123", "exam-42", new ClientInfo("Mozilla/5.0", "0.1.0"));

    public SessionServiceTests()
    {
        using var db = NewDb();
        db.Database.Migrate();
    }

    private ProctoringDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProctoringDbContext>().UseSqlite($"Data Source={_dbPath};Pooling=False").Options);

    /// <summary>A fresh DbContext and service per call, like one HTTP request each.</summary>
    private async Task<T> With<T>(Func<SessionService, Task<T>> action)
    {
        await using var db = NewDb();
        var service = new SessionService(new EfSessionRepository(db), new SessionTokenService(_secret), new SessionIdGenerator(_clock),
            _clock, Options.Create(_options), NullLogger<SessionService>.Instance);
        return await action(service);
    }

    private Task<CreateSessionResult> Create(string? key = Key, CreateSessionRequest? request = null) =>
        With(s => s.CreateAsync(request ?? Request, key, default));

    private Task<Session> Get(CreateSessionResult c, string token) => With(s => s.GetAsync(c.Session.Id, token, default));

    // ------------------------------------------------------------------ storage

    [Fact]
    public async Task Database_contains_no_plaintext_token_or_idempotency_key()
    {
        var idempotent = await Create();
        var plain = await Create(key: null, Request with { CandidateId = "cand-2" });

        await using (var db = NewDb())
        {
            var row = await db.Sessions.SingleAsync(x => x.Id == idempotent.Session.Id);
            Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(idempotent.Token)), row.TokenHash);
            Assert.Equal(32, row.IdempotencyKeyHash!.Length);
            Assert.Equal(32, row.TokenDerivationSalt!.Length);
            var plainRow = await db.Sessions.SingleAsync(x => x.Id == plain.Session.Id);
            Assert.Null(plainRow.TokenDerivationSalt); // ordinary sessions have a purely random token
            Assert.Null(plainRow.IdempotencyKeyHash);
        }
        var file = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(_dbPath));
        foreach (var secret in new[] { idempotent.Token, idempotent.Token[4..], plain.Token[4..], Key })
            Assert.DoesNotContain(secret, file);
    }

    [Fact]
    public async Task Session_survives_a_new_database_context()
    {
        var created = await Create(key: null);
        var loaded = await Get(created, created.Token);
        Assert.Equal("cand-123", loaded.CandidateId);
        Assert.Equal(DateTimeKind.Utc, loaded.CreatedAt.Kind);
        Assert.Equal(created.Session.CreatedAt, loaded.CreatedAt);
    }

    // ------------------------------------------------------------------ idempotency

    [Fact]
    public async Task Lost_first_response_retry_returns_the_same_session_and_token()
    {
        // The first response never reaches the client (we discard everything but what the server stored).
        var lost = await Create();
        _clock.Advance(TimeSpan.FromSeconds(30));

        var retry = await Create();

        Assert.True(retry.Replayed);
        Assert.Equal(lost.Session.Id, retry.Session.Id);
        Assert.Equal(lost.Token, retry.Token); // the token the client never saw
        await Get(retry, retry.Token);
    }

    [Fact]
    public async Task Retry_after_the_client_started_using_the_token_does_not_invalidate_it()
    {
        var first = await Create();
        await Get(first, first.Token); // client is using the session

        var retry = await Create(); // e.g. an automatic network retry of the same request

        Assert.Equal(first.Token, retry.Token);
        await Get(first, first.Token); // still valid
        var ended = await With(s => s.EndAsync(first.Session.Id, first.Token, default));
        Assert.Equal(SessionStatus.Ended, ended.Status);
    }

    [Fact]
    public async Task Concurrent_creates_with_the_same_key_return_one_session_and_one_token()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => Create())));

        Assert.Single(results.Select(r => r.Session.Id).Distinct());
        Assert.Single(results.Select(r => r.Token).Distinct());
        Assert.Single(results, r => !r.Replayed);
        await using var db = NewDb();
        Assert.Equal(1, await db.Sessions.CountAsync());
    }

    [Fact]
    public async Task Same_key_with_a_different_request_is_a_conflict()
    {
        await Create();
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => Create(request: Request with { ExamId = "exam-other" }));
    }

    [Fact]
    public async Task Replay_after_the_window_is_refused_and_the_salt_is_erased()
    {
        var first = await Create();
        _clock.Advance(TimeSpan.FromMinutes(61));

        var ex = await Assert.ThrowsAsync<IdempotentReplayUnavailableException>(() => Create());
        Assert.Contains("window", ex.Message);
        await Get(first, first.Token); // the session itself still works

        await using var db = NewDb();
        Assert.Null((await db.Sessions.SingleAsync()).TokenDerivationSalt);
        Assert.Equal(1, await db.Sessions.CountAsync()); // the key never creates a second session
    }

    [Fact]
    public async Task Replay_of_an_ended_session_is_refused()
    {
        var first = await Create();
        await With(s => s.EndAsync(first.Session.Id, first.Token, default));
        var ex = await Assert.ThrowsAsync<IdempotentReplayUnavailableException>(() => Create());
        Assert.Contains("ended", ex.Message);
    }

    [Fact]
    public async Task Replay_after_the_server_secret_changed_is_refused_instead_of_issuing_a_new_token()
    {
        var first = await Create();
        _secret = SessionTokenServiceTests.NewSecret(); // e.g. restart with an ephemeral secret
        var ex = await Assert.ThrowsAsync<IdempotentReplayUnavailableException>(() => Create());
        Assert.Contains("secret", ex.Message);
        await Get(first, first.Token); // the original token keeps working
    }

    [Fact]
    public async Task Reading_a_session_after_the_window_erases_the_salt()
    {
        var first = await Create();
        _clock.Advance(TimeSpan.FromMinutes(90));
        await Get(first, first.Token);
        await using var db = NewDb();
        Assert.Null((await db.Sessions.SingleAsync()).TokenDerivationSalt);
    }

    // ------------------------------------------------------------------ authorization, end, expiry

    [Fact]
    public async Task Wrong_token_unknown_session_and_cross_session_token_all_look_like_not_found()
    {
        var a = await Create(key: null);
        var b = await Create(key: null, Request with { CandidateId = "cand-999" });

        await Assert.ThrowsAsync<SessionNotFoundException>(() => Get(a, b.Token));
        await Assert.ThrowsAsync<SessionNotFoundException>(() => Get(a, "tok_" + new string('A', 43)));
        await Assert.ThrowsAsync<SessionNotFoundException>(() => With(s => s.GetAsync("ses_01JUNKNOWNUNKNOWNUNKNOWNXX", a.Token, default)));
    }

    [Fact]
    public async Task End_is_idempotent_and_persisted()
    {
        var c = await Create(key: null);
        _clock.Advance(TimeSpan.FromMinutes(40));
        var first = await With(s => s.EndAsync(c.Session.Id, c.Token, default));
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = await With(s => s.EndAsync(c.Session.Id, c.Token, default));

        Assert.Equal(SessionEndReason.Candidate, second.EndReason);
        Assert.Equal(first.EndedAt, second.EndedAt);
        Assert.Equal(c.Session.CreatedAt.AddMinutes(40), second.EndedAt);
    }

    [Fact]
    public async Task Session_expires_after_max_duration_and_the_change_is_stored()
    {
        var c = await Create(key: null);
        _clock.Advance(TimeSpan.FromHours(7));
        var expired = await Get(c, c.Token);
        Assert.Equal(SessionEndReason.Expired, expired.EndReason);
        Assert.Equal(c.Session.CreatedAt.AddHours(6), expired.EndedAt);

        await using var db = NewDb();
        Assert.Equal(SessionStatus.Ended, (await db.Sessions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Token_is_rejected_after_ttl_following_the_end()
    {
        var c = await Create(key: null);
        await With(s => s.EndAsync(c.Session.Id, c.Token, default));
        _clock.Advance(TimeSpan.FromHours(23));
        await Get(c, c.Token);
        _clock.Advance(TimeSpan.FromHours(2));
        await Assert.ThrowsAsync<TokenExpiredException>(() => Get(c, c.Token));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* best effort */ }
    }
}
