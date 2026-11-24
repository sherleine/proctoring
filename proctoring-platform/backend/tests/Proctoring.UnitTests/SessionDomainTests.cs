using Proctoring.Domain.Sessions;

namespace Proctoring.UnitTests;

public class SessionDomainTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
    private static readonly byte[] Hash = new byte[32];

    private static Session NewSession(TimeSpan? max = null) =>
        Session.Start("ses_01JABCDEFGHJKMNPQRSTVWXYZ", "cand-1", "exam-1", Hash, T0, max ?? TimeSpan.FromHours(6));

    [Fact]
    public void Start_creates_active_session_with_expiry()
    {
        var s = NewSession();
        Assert.Equal(SessionStatus.Active, s.Status);
        Assert.Equal(T0, s.CreatedAt);
        Assert.Equal(T0.AddHours(6), s.ExpiresAt);
        Assert.Null(s.EndedAt);
        Assert.Null(s.EndReason);
    }

    [Fact]
    public void End_by_candidate_records_time_and_reason()
    {
        var s = NewSession();
        Assert.True(s.End(SessionEndReason.Candidate, T0.AddMinutes(30)));
        Assert.Equal(SessionStatus.Ended, s.Status);
        Assert.Equal(T0.AddMinutes(30), s.EndedAt);
        Assert.Equal(SessionEndReason.Candidate, s.EndReason);
    }

    [Fact]
    public void End_is_idempotent()
    {
        var s = NewSession();
        s.End(SessionEndReason.Candidate, T0.AddMinutes(30));
        Assert.False(s.End(SessionEndReason.Candidate, T0.AddMinutes(45)));
        Assert.Equal(T0.AddMinutes(30), s.EndedAt); // unchanged
    }

    [Fact]
    public void ExpireIfDue_does_nothing_before_expiry()
    {
        var s = NewSession();
        Assert.False(s.ExpireIfDue(T0.AddHours(5.9)));
        Assert.Equal(SessionStatus.Active, s.Status);
    }

    [Fact]
    public void ExpireIfDue_ends_at_expiry_time_not_at_detection_time()
    {
        var s = NewSession();
        Assert.True(s.ExpireIfDue(T0.AddHours(9)));
        Assert.Equal(SessionStatus.Ended, s.Status);
        Assert.Equal(SessionEndReason.Expired, s.EndReason);
        Assert.Equal(s.ExpiresAt, s.EndedAt);
    }

    [Fact]
    public void ExpireIfDue_does_not_change_a_session_ended_by_the_candidate()
    {
        var s = NewSession();
        s.End(SessionEndReason.Candidate, T0.AddHours(1));
        Assert.False(s.ExpireIfDue(T0.AddHours(9)));
        Assert.Equal(SessionEndReason.Candidate, s.EndReason);
    }

    [Theory]
    [InlineData(0.5, true)] // active
    [InlineData(1 + 23.9, true)] // ended 1 h, within 24 h TTL
    [InlineData(1 + 24.1, false)] // TTL passed
    public void Token_usable_until_ttl_after_end(double hoursAfterStart, bool expected)
    {
        var s = NewSession();
        if (hoursAfterStart > 1) s.End(SessionEndReason.Candidate, T0.AddHours(1));
        Assert.Equal(expected, s.IsTokenUsableAt(T0.AddHours(hoursAfterStart), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void Token_ttl_counts_from_expiry_for_expired_sessions()
    {
        var s = NewSession(TimeSpan.FromHours(2));
        Assert.True(s.IsTokenUsableAt(T0.AddHours(2 + 23), TimeSpan.FromHours(24)));
        Assert.False(s.IsTokenUsableAt(T0.AddHours(2 + 25), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void Rejects_invalid_input()
    {
        Assert.Throws<ArgumentException>(() => Session.Start("ses_x", "cand", "exam", new byte[16], T0, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentException>(() => Session.Start("ses_x", "", "exam", Hash, T0, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentException>(() => Session.Start("ses_x", "cand", "exam", Hash, DateTime.Now, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Session.Start("ses_x", "cand", "exam", Hash, T0, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => NewSession().EnableIdempotentReplay(new byte[31], Hash, Salt, T0.AddHours(1)));
        Assert.Throws<ArgumentException>(() => NewSession().EnableIdempotentReplay(Hash, Hash, new byte[8], T0.AddHours(1)));
    }

    private static readonly byte[] Salt = new byte[32];

    private static Session Replayable(TimeSpan window)
    {
        var s = NewSession();
        s.EnableIdempotentReplay(Hash, Hash, Salt, T0 + window);
        return s;
    }

    [Fact]
    public void Idempotent_replay_allowed_only_inside_the_window_while_active()
    {
        var s = Replayable(TimeSpan.FromMinutes(60));
        Assert.True(s.CanReplayCreate(T0.AddMinutes(59)));
        Assert.False(s.CanReplayCreate(T0.AddMinutes(60)));
        Assert.False(NewSession().CanReplayCreate(T0)); // non-idempotent sessions are never replayed
    }

    [Fact]
    public void Ending_the_session_erases_the_derivation_salt()
    {
        var s = Replayable(TimeSpan.FromMinutes(60));
        s.End(SessionEndReason.Candidate, T0.AddMinutes(5));
        Assert.Null(s.TokenDerivationSalt);
        Assert.False(s.CanReplayCreate(T0.AddMinutes(6)));
    }

    [Fact]
    public void Salt_is_erased_once_the_window_has_passed()
    {
        var s = Replayable(TimeSpan.FromMinutes(60));
        Assert.False(s.ForgetTokenDerivationIfNotReplayable(T0.AddMinutes(30)));
        Assert.NotNull(s.TokenDerivationSalt);
        Assert.True(s.ForgetTokenDerivationIfNotReplayable(T0.AddMinutes(61)));
        Assert.Null(s.TokenDerivationSalt);
        Assert.NotNull(s.IdempotencyKeyHash); // the key hash stays, so the key cannot create a second session
    }

    [Fact]
    public void Idempotency_can_only_be_configured_once()
    {
        var s = Replayable(TimeSpan.FromMinutes(60));
        Assert.Throws<InvalidOperationException>(() => s.EnableIdempotentReplay(Hash, Hash, Salt, T0.AddHours(1)));
    }
}
