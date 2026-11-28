"""VoiceReferenceStore against an in-memory FakeRedis: one reference per (examid, emailid), first write wins."""
import json

import numpy as np

from app.services.VoiceReferenceStoreService import VoiceReferenceStore
from tests.helpers import FakeRedis

A, B = np.array([1.0, 0.0], dtype=np.float32), np.array([0.0, 1.0], dtype=np.float32)


def test_get_returns_none_when_nothing_registered():
    assert VoiceReferenceStore(FakeRedis(), ttl_seconds=60).get("EXAM_001", "a@example.com") is None


def test_register_then_get_round_trips_embedding_and_model_version():
    store = VoiceReferenceStore(FakeRedis(), ttl_seconds=60)
    assert store.register_if_absent("EXAM_001", "a@example.com", A, "campplus-test") is True
    ref = store.get("EXAM_001", "a@example.com")
    np.testing.assert_allclose(ref.embedding, A, atol=1e-6)
    assert ref.model_version == "campplus-test" and ref.registered_at


def test_second_register_is_a_no_op_and_keeps_the_first_voice():
    store = VoiceReferenceStore(FakeRedis(), ttl_seconds=60)
    store.register_if_absent("EXAM_001", "a@example.com", A, "m")
    assert store.register_if_absent("EXAM_001", "a@example.com", B, "m") is False
    np.testing.assert_allclose(store.get("EXAM_001", "a@example.com").embedding, A, atol=1e-6)


def test_references_are_independent_per_exam_and_email():
    store = VoiceReferenceStore(FakeRedis(), ttl_seconds=60)
    store.register_if_absent("EXAM_001", "a@example.com", A, "m")
    assert store.register_if_absent("EXAM_001", "b@example.com", B, "m") is True
    assert store.register_if_absent("EXAM_002", "a@example.com", B, "m") is True
    np.testing.assert_allclose(store.get("EXAM_001", "b@example.com").embedding, B, atol=1e-6)


def test_key_format_and_ttl():
    redis = FakeRedis()
    VoiceReferenceStore(redis, ttl_seconds=3600).register_if_absent("EXAM_001", "a@example.com", A, "m")
    key = "voice_ref:EXAM_001:a@example.com"
    assert redis.ttls[key] == 3600
    assert set(json.loads(redis.data[key])) == {"embedding", "registeredAt", "modelVersion"}
