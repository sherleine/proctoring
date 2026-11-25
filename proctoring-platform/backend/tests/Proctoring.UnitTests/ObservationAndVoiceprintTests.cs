using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Proctoring.Application.Errors;
using Proctoring.Application.Events;
using Proctoring.Domain.Events;
using Proctoring.Infrastructure.Notifications;
using Proctoring.Infrastructure.Persistence;
using Proctoring.Infrastructure.Validation;
using Proctoring.Tests.Shared;

namespace Proctoring.UnitTests;

/// <summary>
/// Contract 1.1: candidate voice reference (enrolment) and AI observations - storage, privacy of the embedding,
/// the speaker reference in outgoing messages, idempotency and retention.
/// </summary>
public sealed class ObservationAndVoiceprintTests : IDisposable
{
    private static readonly JsonSchemaContractValidator Contracts = new(Path.Combine(AppContext.BaseDirectory, "contracts", "schemas"));
    private static readonly double[] Embedding = Enumerable.Range(0, 32).Select(i => Math.Round(Math.Sin(i), 4)).ToArray();
    private readonly EventHarness _h = new();
    public void Dispose() => _h.Dispose();

    private DateTime Now => _h.Clock.GetUtcNow().UtcDateTime;

    private async Task<(string SessionId, string Token, Guid EventId)> UploadAsync(string eventType = "speech", double seconds = 2,
        (string SessionId, string Token)? session = null)
    {
        var (sid, token) = session ?? await NewSessionAsync();
        var wav = TestAudio.Wav(seconds);
        var id = Guid.NewGuid();
        var result = await _h.UploadAsync(sid, token, TestAudio.Metadata(wav, id, Now, eventType: eventType), wav);
        Assert.Equal(AudioEventStatus.Queued, result.Event.Status);
        return (sid, token, id);
    }

    private async Task<(string, string)> NewSessionAsync()
    {
        var (s, t) = await _h.NewSessionAsync();
        return (s.Id, t);
    }

    private async Task<ProcessingOutcome> ApplyAsync(JsonObject payload)
    {
        await using var db = _h.NewDb();
        var service = new ResultIngestionService(new EfAudioEventRepository(db, new InProcessEventUpdateNotifier()), Contracts,
            _h.Clock, NullLogger<ResultIngestionService>.Instance, new EfObservationRepository(db), new EfVoiceprintRepository(db));
        var message = new ProcessingMessage("audio.analysis.completed", "1", Guid.NewGuid().ToString(),
            (string)payload["sessionId"]!, (string)payload["eventId"]!, payload.ToJsonString());
        return await service.ApplyAsync(message, 1, default);
    }

    private static JsonObject EnrolmentResult(string sessionId, Guid eventId, string status = "accepted")
    {
        var p = TestMessages.Completed(sessionId, eventId);
        p["schemaVersion"] = "1.1";
        p["enrolment"] = new JsonObject
        {
            ["status"] = status, ["reason"] = null, ["speechSeconds"] = 14.2, ["windows"] = 8, ["selfConsistency"] = 0.89,
            ["minWindowSimilarity"] = 0.8, ["speechRmsDbfs"] = -24.0, ["clippedRatio"] = 0.0,
            ["embedding"] = status == "rejected" ? null : new JsonArray(Embedding.Select(x => (JsonNode)x).ToArray()),
            ["embeddingModel"] = "campplus-voxceleb-en:sha256-98daf9bcf1c8",
        };
        p["observations"] = new JsonArray();
        return p;
    }

    private static JsonObject Observation(string sessionId, Guid eventId, string type = "NON_CANDIDATE_VOICE", double start = 1.0,
        Guid? id = null) => new()
    {
        ["observationId"] = (id ?? Guid.NewGuid()).ToString(), ["sessionId"] = sessionId, ["eventId"] = eventId.ToString(),
        ["observationType"] = type, ["startSeconds"] = start, ["endSeconds"] = start + 3.5, ["confidence"] = 0.82,
        ["confidenceKind"] = "model-score", ["supportingEvidence"] = new JsonObject { ["similarityToCandidate"] = 0.05 },
        ["modelName"] = "CAM++ (3D-Speaker, VoxCeleb)", ["modelVersion"] = "campplus-voxceleb-en:sha256-98daf9bcf1c8",
        ["createdAt"] = "2026-11-21T10:00:05.000Z",
    };

    private static JsonObject SpeechResult(string sessionId, Guid eventId, params JsonObject[] observations)
    {
        var p = TestMessages.Completed(sessionId, eventId);
        p["schemaVersion"] = "1.1";
        p["observations"] = new JsonArray(observations.Select(o => (JsonNode)o).ToArray());
        return p;
    }

    // ------------------------------------------------------------------ enrolment

    [Fact]
    public async Task Enrolment_result_stores_the_voiceprint_and_removes_the_embedding_from_the_event_result()
    {
        var (sid, _, eventId) = await UploadAsync("enrolment", seconds: 15);
        Assert.Equal(ProcessingDisposition.Applied, (await ApplyAsync(EnrolmentResult(sid, eventId))).Disposition);

        await using var db = _h.NewDb();
        var voiceprint = await db.Voiceprints.SingleAsync(v => v.SessionId == sid);
        Assert.Equal("accepted", voiceprint.Status);
        Assert.True(voiceprint.IsUsable);
        Assert.Equal(eventId, voiceprint.EnrolmentEventId);
        Assert.Equal(Embedding.Length, JsonNode.Parse(voiceprint.EmbeddingJson!)!.AsArray().Count);

        var stored = JsonNode.Parse((await _h.LoadAsync(sid, eventId)).ResultJson!)!;
        Assert.Null(stored["enrolment"]!["embedding"]);
        Assert.True((bool)stored["enrolment"]!["embeddingStored"]!);
        Assert.Empty(Contracts.Validate("analysis-result.v1.json", stored)); // still a valid document
    }

    [Fact]
    public async Task Rejected_enrolment_keeps_no_embedding_and_is_not_used()
    {
        var (sid, token, eventId) = await UploadAsync("enrolment", seconds: 15);
        await ApplyAsync(EnrolmentResult(sid, eventId, status: "rejected"));
        _h.Queue.Messages.Clear();
        await UploadAsync("speech", session: (sid, token));
        var message = JsonNode.Parse(Assert.Single(_h.Queue.Messages).PayloadJson)!;
        Assert.Null(message["speakerReference"]);
    }

    [Fact]
    public async Task Speech_events_after_an_accepted_enrolment_carry_the_reference_but_the_enrolment_itself_does_not()
    {
        var (sid, token, enrolmentId) = await UploadAsync("enrolment", seconds: 15);
        Assert.Null(JsonNode.Parse(_h.Queue.Messages.Single().PayloadJson)!["speakerReference"]);
        await ApplyAsync(EnrolmentResult(sid, enrolmentId));

        _h.Queue.Messages.Clear();
        await UploadAsync("speech", session: (sid, token));
        var payload = JsonNode.Parse(Assert.Single(_h.Queue.Messages).PayloadJson)!;
        var reference = payload["speakerReference"]!;
        Assert.Equal(enrolmentId.ToString(), (string)reference["enrolmentEventId"]!);
        Assert.Equal(Embedding.Length, reference["embedding"]!.AsArray().Count);
        Assert.Empty(Contracts.Validate("audio-event-message.v1.json", payload));
    }

    [Fact]
    public async Task A_voiceprint_never_crosses_into_another_session()
    {
        var (sid, _, enrolmentId) = await UploadAsync("enrolment", seconds: 15);
        await ApplyAsync(EnrolmentResult(sid, enrolmentId));
        _h.Queue.Messages.Clear();
        await UploadAsync("speech"); // a different session
        Assert.Null(JsonNode.Parse(Assert.Single(_h.Queue.Messages).PayloadJson)!["speakerReference"]);
    }

    [Fact]
    public async Task A_newer_enrolment_replaces_the_reference()
    {
        var (sid, token, first) = await UploadAsync("enrolment", seconds: 15);
        await ApplyAsync(EnrolmentResult(sid, first));
        var (_, _, second) = await UploadAsync("enrolment", seconds: 15, session: (sid, token));
        await ApplyAsync(EnrolmentResult(sid, second, status: "uncertain"));
        await using var db = _h.NewDb();
        var v = await db.Voiceprints.SingleAsync(x => x.SessionId == sid);
        Assert.Equal(second, v.EnrolmentEventId);
        Assert.Equal("uncertain", v.Status);
        Assert.False(v.IsUsable);
    }

    [Fact]
    public async Task Enrolment_on_a_speech_event_is_ignored()
    {
        var (sid, _, eventId) = await UploadAsync("speech");
        await ApplyAsync(EnrolmentResult(sid, eventId));
        await using var db = _h.NewDb();
        Assert.False(await db.Voiceprints.AnyAsync(v => v.SessionId == sid));
    }

    [Fact]
    public async Task Enrolment_may_be_up_to_30_s_but_a_speech_event_may_not()
    {
        await UploadAsync("enrolment", seconds: 25);
        var (sid, token) = await NewSessionAsync();
        var wav = TestAudio.Wav(25);
        var ex = await Assert.ThrowsAsync<RequestValidationException>(() =>
            _h.UploadAsync(sid, token, TestAudio.Metadata(wav, Guid.NewGuid(), Now), wav));
        Assert.Contains("audio", ex.Errors.Keys);
    }

    // ------------------------------------------------------------------ observations

    [Fact]
    public async Task Observations_are_stored_as_rows_with_absolute_times_and_a_redelivery_adds_nothing()
    {
        var (sid, _, eventId) = await UploadAsync();
        var obsId = Guid.NewGuid();
        var payload = SpeechResult(sid, eventId, Observation(sid, eventId, start: 1.0, id: obsId),
            Observation(sid, eventId, "MULTIPLE_SPEAKERS_DETECTED", start: 0.2));
        Assert.Equal(ProcessingDisposition.Applied, (await ApplyAsync(payload)).Disposition);
        Assert.Equal(ProcessingDisposition.Ignored, (await ApplyAsync(payload)).Disposition);

        await using var db = _h.NewDb();
        var rows = await db.Observations.Where(o => o.SessionId == sid).OrderBy(o => o.StartSeconds).ToListAsync();
        Assert.Equal(2, rows.Count);
        var row = rows.Single(o => o.Id == obsId);
        Assert.Equal("NON_CANDIDATE_VOICE", row.ObservationType);
        Assert.Equal("model-score", row.ConfidenceKind);
        var e = await _h.LoadAsync(sid, eventId);
        Assert.Equal(e.StartedAt.AddSeconds(1.0), row.OccurredAt);
    }

    [Fact]
    public async Task Informational_records_and_legacy_names_are_stored_with_the_right_category()
    {
        var (sid, _, eventId) = await UploadAsync();
        var speech = Observation(sid, eventId, "SPEECH_DETECTED", start: 0.1);
        speech["category"] = "informational";
        var legacy = Observation(sid, eventId, "MULTIPLE_SPEAKERS", start: 0.3); // deprecated alias, no category
        Assert.Equal(ProcessingDisposition.Applied, (await ApplyAsync(SpeechResult(sid, eventId, speech, legacy))).Disposition);
        await using var db = _h.NewDb();
        var rows = await db.Observations.Where(o => o.SessionId == sid).ToDictionaryAsync(o => o.ObservationType, o => o.Category);
        Assert.Equal("informational", rows["SPEECH_DETECTED"]);
        Assert.Equal("review", rows["MULTIPLE_SPEAKERS"]);
    }

    [Fact]
    public async Task An_observation_naming_another_event_is_ignored()
    {
        var (sid, _, eventId) = await UploadAsync();
        var payload = SpeechResult(sid, eventId, Observation(sid, Guid.NewGuid()));
        Assert.Equal(ProcessingDisposition.Applied, (await ApplyAsync(payload)).Disposition);
        await using var db = _h.NewDb();
        Assert.False(await db.Observations.AnyAsync(o => o.SessionId == sid));
    }

    [Fact]
    public async Task An_invalid_observation_dead_letters_the_message()
    {
        var (sid, _, eventId) = await UploadAsync();
        var bad = Observation(sid, eventId);
        bad["confidence"] = 1.7;
        var outcome = await ApplyAsync(SpeechResult(sid, eventId, bad));
        Assert.Equal(ProcessingDisposition.DeadLetter, outcome.Disposition);
    }

    [Fact]
    public async Task A_version_1_0_result_without_observations_is_still_accepted()
    {
        var (sid, _, eventId) = await UploadAsync();
        Assert.Equal(ProcessingDisposition.Applied, (await ApplyAsync(TestMessages.Completed(sid, eventId))).Disposition);
    }

    // ------------------------------------------------------------------ retention

    [Fact]
    public async Task Voiceprints_are_deleted_after_their_retention()
    {
        var (sid, _, eventId) = await UploadAsync("enrolment", seconds: 15);
        await ApplyAsync(EnrolmentResult(sid, eventId));
        _h.Clock.Advance(_h.EventOptions.VoiceprintRetention + TimeSpan.FromMinutes(1));
        await using (var s = _h.NewScope())
            Assert.Equal(1, await s.Maintenance.ApplyVoiceprintRetentionAsync(default));
        await using var db = _h.NewDb();
        Assert.False(await db.Voiceprints.AnyAsync(v => v.SessionId == sid));
    }
}
