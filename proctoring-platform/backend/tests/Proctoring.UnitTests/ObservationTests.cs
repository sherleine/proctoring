using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Proctoring.Application.Events;
using Proctoring.Infrastructure.Notifications;
using Proctoring.Infrastructure.Persistence;
using Proctoring.Infrastructure.Validation;
using Proctoring.Tests.Shared;

namespace Proctoring.UnitTests;

/// <summary>Contract 1.1: AI observations stored as rows - shape, categories, idempotency, ownership, validation.</summary>
public sealed class ObservationTests : IDisposable
{
    private static readonly JsonSchemaContractValidator Contracts = new(Path.Combine(AppContext.BaseDirectory, "contracts", "schemas"));
    private readonly EventHarness _h = new();
    public void Dispose() => _h.Dispose();

    private DateTime Now => _h.Clock.GetUtcNow().UtcDateTime;

    private async Task<(string SessionId, Guid EventId)> UploadAsync()
    {
        var (session, token) = await _h.NewSessionAsync();
        var wav = TestAudio.Wav(2);
        var id = Guid.NewGuid();
        await _h.UploadAsync(session.Id, token, TestAudio.Metadata(wav, id, Now), wav);
        return (session.Id, id);
    }

    private async Task<ProcessingOutcome> ApplyAsync(JsonObject payload)
    {
        await using var db = _h.NewDb();
        var service = new ResultIngestionService(new EfAudioEventRepository(db, new InProcessEventUpdateNotifier()), Contracts,
            _h.Clock, NullLogger<ResultIngestionService>.Instance, new EfObservationRepository(db));
        var message = new ProcessingMessage("audio.analysis.completed", "1", Guid.NewGuid().ToString(),
            (string)payload["sessionId"]!, (string)payload["eventId"]!, payload.ToJsonString());
        return await service.ApplyAsync(message, 1, default);
    }

    private static JsonObject Observation(string sessionId, Guid eventId, string type = "BACKGROUND_CONVERSATION_DETECTED",
        double start = 1.0, Guid? id = null) => new()
    {
        ["observationId"] = (id ?? Guid.NewGuid()).ToString(), ["sessionId"] = sessionId, ["eventId"] = eventId.ToString(),
        ["observationType"] = type, ["startSeconds"] = start, ["endSeconds"] = start + 3.5, ["confidence"] = 0.82,
        ["confidenceKind"] = "rule", ["supportingEvidence"] = new JsonObject { ["levelBelowMainSpeakerDb"] = 18.5 },
        ["modelName"] = "pyannote segmentation + CAM++", ["modelVersion"] = "test",
        ["createdAt"] = "2026-11-21T10:00:05.000Z",
    };

    private static JsonObject SpeechResult(string sessionId, Guid eventId, params JsonObject[] observations)
    {
        var p = TestMessages.Completed(sessionId, eventId);
        p["schemaVersion"] = "1.1";
        p["observations"] = new JsonArray(observations.Select(o => (JsonNode)o).ToArray());
        return p;
    }

    [Fact]
    public async Task Observations_are_stored_as_rows_with_absolute_times_and_a_redelivery_adds_nothing()
    {
        var (sid, eventId) = await UploadAsync();
        var obsId = Guid.NewGuid();
        var payload = SpeechResult(sid, eventId, Observation(sid, eventId, start: 1.0, id: obsId),
            Observation(sid, eventId, "MULTIPLE_SPEAKERS_DETECTED", start: 0.2));
        Assert.Equal(ProcessingDisposition.Applied, (await ApplyAsync(payload)).Disposition);
        Assert.Equal(ProcessingDisposition.Ignored, (await ApplyAsync(payload)).Disposition);

        await using var db = _h.NewDb();
        var rows = await db.Observations.Where(o => o.SessionId == sid).OrderBy(o => o.StartSeconds).ToListAsync();
        Assert.Equal(2, rows.Count);
        var row = rows.Single(o => o.Id == obsId);
        Assert.Equal("BACKGROUND_CONVERSATION_DETECTED", row.ObservationType);
        Assert.Equal("rule", row.ConfidenceKind);
        var e = await _h.LoadAsync(sid, eventId);
        Assert.Equal(e.StartedAt.AddSeconds(1.0), row.OccurredAt);
    }

    [Fact]
    public async Task Informational_records_and_legacy_names_are_stored_with_the_right_category()
    {
        var (sid, eventId) = await UploadAsync();
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
        var (sid, eventId) = await UploadAsync();
        var payload = SpeechResult(sid, eventId, Observation(sid, Guid.NewGuid()));
        Assert.Equal(ProcessingDisposition.Applied, (await ApplyAsync(payload)).Disposition);
        await using var db = _h.NewDb();
        Assert.False(await db.Observations.AnyAsync(o => o.SessionId == sid));
    }

    [Fact]
    public async Task An_invalid_observation_dead_letters_the_message()
    {
        var (sid, eventId) = await UploadAsync();
        var bad = Observation(sid, eventId);
        bad["confidence"] = 1.7;
        Assert.Equal(ProcessingDisposition.DeadLetter, (await ApplyAsync(SpeechResult(sid, eventId, bad))).Disposition);
    }

    [Fact]
    public async Task A_version_1_0_result_without_observations_is_still_accepted()
    {
        var (sid, eventId) = await UploadAsync();
        Assert.Equal(ProcessingDisposition.Applied, (await ApplyAsync(TestMessages.Completed(sid, eventId))).Disposition);
    }
}
