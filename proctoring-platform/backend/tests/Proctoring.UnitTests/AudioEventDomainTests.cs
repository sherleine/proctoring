using Proctoring.Domain.Events;

namespace Proctoring.UnitTests;

public class AudioEventDomainTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);

    internal static AudioEvent NewEvent() => AudioEvent.Receive(
        "ses_01JABCDEFGHJKMNPQRSTVWXYZ", Guid.NewGuid(), "cand-1", "exam-1", 0,
        T0, T0.AddSeconds(2), T0.AddSeconds(0.5), 2.0, 16000, 1, 64044, new string('a', 64), new string('b', 64), "{}",
        "ses_01JABCDEFGHJKMNPQRSTVWXYZ/" + new string('c', 32) + ".wav", T0);

    [Fact]
    public void New_events_are_RECEIVED()
    {
        var e = NewEvent();
        Assert.Equal(AudioEventStatus.Received, e.Status);
        Assert.Equal(T0, e.ReceivedAt);
        Assert.Null(e.QueuedAt);
        Assert.Equal(0, e.Attempts);
    }

    [Theory]
    [InlineData(AudioEventStatus.Received, AudioEventStatus.Queued, true)]
    [InlineData(AudioEventStatus.Received, AudioEventStatus.Failed, true)]
    [InlineData(AudioEventStatus.Received, AudioEventStatus.Completed, false)] // never "analysed" without processing
    [InlineData(AudioEventStatus.Received, AudioEventStatus.Processing, false)]
    [InlineData(AudioEventStatus.Queued, AudioEventStatus.Processing, true)]
    [InlineData(AudioEventStatus.Queued, AudioEventStatus.DeadLettered, true)]
    [InlineData(AudioEventStatus.Processing, AudioEventStatus.Completed, true)]
    [InlineData(AudioEventStatus.Processing, AudioEventStatus.Queued, true)] // redelivery
    [InlineData(AudioEventStatus.Failed, AudioEventStatus.Queued, true)] // retry
    [InlineData(AudioEventStatus.Completed, AudioEventStatus.Queued, false)]
    [InlineData(AudioEventStatus.DeadLettered, AudioEventStatus.Queued, false)]
    public void Lifecycle_allows_only_documented_transitions(AudioEventStatus from, AudioEventStatus to, bool allowed) =>
        Assert.Equal(allowed, AudioEvent.CanTransition(from, to));

    [Fact]
    public void MarkQueued_then_MarkQueued_again_is_refused()
    {
        var e = NewEvent();
        e.MarkQueued(T0.AddSeconds(1));
        Assert.Equal(AudioEventStatus.Queued, e.Status);
        Assert.Equal(T0.AddSeconds(1), e.QueuedAt);
        Assert.Throws<InvalidOperationException>(() => e.MarkQueued(T0.AddSeconds(2)));
    }

    [Fact]
    public void Failure_records_code_and_message()
    {
        var e = NewEvent();
        e.MarkFailed("AUDIO_FILE_MISSING", "not stored", T0.AddMinutes(11));
        Assert.Equal(AudioEventStatus.Failed, e.Status);
        Assert.Equal("AUDIO_FILE_MISSING", e.ErrorCode);
    }

    [Fact]
    public void Same_upload_means_same_metadata_and_audio_hashes()
    {
        var e = NewEvent();
        Assert.True(e.IsSameUpload(new string('b', 64), new string('a', 64)));
        Assert.False(e.IsSameUpload(new string('b', 64), new string('d', 64)));
        Assert.False(e.IsSameUpload(new string('e', 64), new string('a', 64)));
    }

    [Fact]
    public void Receive_rejects_inconsistent_input()
    {
        Assert.Throws<ArgumentException>(() => AudioEvent.Receive("ses_x", Guid.Empty, "c", "e", 0, T0, T0.AddSeconds(1), T0, 1, 16000, 1, 100, "a", "b", "{}", "k", T0));
        Assert.Throws<ArgumentException>(() => AudioEvent.Receive("ses_x", Guid.NewGuid(), "c", "e", 0, T0, T0, T0, 1, 16000, 1, 100, "a", "b", "{}", "k", T0));
        Assert.Throws<ArgumentException>(() => AudioEvent.Receive("ses_x", Guid.NewGuid(), "c", "e", 0, T0, T0.AddSeconds(1), T0, 1, 16000, 1, 100, "a", "b", "{}", "", T0));
    }
}
