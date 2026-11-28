"""Persists the one registered reference voice per (examid, emailid) across separate requests, in Redis
(already a dependency - see app/clients/redis_client.py; this uses the same connection, not a second one).

Key: voice_ref:{examid}:{emailid} -> JSON {"embedding": [...floats...], "registeredAt": iso, "modelVersion": str}.
A TTL keeps an abandoned/old exam's reference from living forever, without needing an explicit cleanup job -
it's long enough that no real exam should hit it (see Settings.voice_reference_ttl_seconds).
"""
from __future__ import annotations

import json
from dataclasses import dataclass

import numpy as np

from app.utils.time_utils import utc_now_iso

KEY_PREFIX = "voice_ref"


@dataclass(frozen=True)
class ReferenceVoice:
    embedding: np.ndarray
    registered_at: str
    model_version: str


def _key(exam_id: str, email_id: str) -> str:
    return f"{KEY_PREFIX}:{exam_id}:{email_id}"


class VoiceReferenceStore:
    def __init__(self, client, ttl_seconds: int):
        self.client = client
        self.ttl_seconds = ttl_seconds

    def get(self, exam_id: str, email_id: str) -> ReferenceVoice | None:
        raw = self.client.get(_key(exam_id, email_id))
        if raw is None:
            return None
        data = json.loads(raw)
        return ReferenceVoice(embedding=np.array(data["embedding"], dtype=np.float32),
                              registered_at=data["registeredAt"], model_version=data["modelVersion"])

    def register_if_absent(self, exam_id: str, email_id: str, embedding: np.ndarray, model_version: str) -> bool:
        """Registers the reference voice only if one isn't already set for this examid+emailid (atomic SET NX),
        so two near-simultaneous "first" requests for the same exam/candidate can't both win and overwrite each
        other - whichever reaches Redis first is the one that sticks, the other's register is simply a no-op.
        Returns whether THIS call was the one that registered it."""
        payload = json.dumps({"embedding": [round(float(x), 6) for x in embedding],
                              "registeredAt": utc_now_iso(), "modelVersion": model_version})
        return bool(self.client.set(_key(exam_id, email_id), payload, nx=True, ex=self.ttl_seconds))
