using Proctoring.Contracts.Sessions;
using Proctoring.Domain.Sessions;

namespace Proctoring.Application.Sessions;

public static class SessionMapper
{
    public static SessionResponse ToResponse(Session s, RecordingPolicyOptions policy, string? token = null) => new()
    {
        SessionId = s.Id,
        SessionToken = token,
        CandidateId = s.CandidateId,
        ExamId = s.ExamId,
        Status = s.Status == SessionStatus.Active ? SessionStatusValues.Active : SessionStatusValues.Ended,
        CreatedAt = s.CreatedAt,
        EndedAt = s.EndedAt,
        ExpiresAt = s.ExpiresAt,
        EndReason = s.EndReason switch
        {
            SessionEndReason.Candidate => SessionEndReasonValues.Candidate,
            SessionEndReason.Expired => SessionEndReasonValues.Expired,
            _ => null,
        },
        RecordingPolicy = new RecordingPolicyDto(
            policy.PreEventSeconds,
            policy.SilenceSeconds,
            policy.MaxEventSeconds,
            policy.SplitOverlapSeconds,
            policy.UploadVideo,
            policy.MaxUploadBytes),
    };
}
