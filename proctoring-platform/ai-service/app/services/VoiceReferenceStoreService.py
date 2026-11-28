"""The one registered reference voice per (examid, emailid), in Redis with a TTL.

Key: voice_ref:{examid}:{emailid} -> JSON {"embedding": [...floats...], "registeredAt": iso, "modelVersion": str}.
"""
import json

import numpy as np

from app.utils.time_utils import utc_now_iso

KEY_PREFIX = "voice_ref"


def _key(exam_id: str, email_id: str) -> str:
    return f"{KEY_PREFIX}:{exam_id}:{email_id}"


class VoiceReferenceStore:
    def __init__(self, client, ttl_seconds: int):
        self.client = client
        self.ttl_seconds = ttl_seconds

    def get(self, exam_id: str, email_id: str) -> np.ndarray | None:
        raw = self.client.get(_key(exam_id, email_id))
        return None if raw is None else np.array(json.loads(raw)["embedding"], dtype=np.float32)

    def register_if_absent(self, exam_id: str, email_id: str, embedding: np.ndarray, model_version: str) -> bool:
        """Atomic SET NX: of two simultaneous "first" requests only one registers. Returns whether this call did."""
        payload = json.dumps({"embedding": [round(float(x), 6) for x in embedding],
                              "registeredAt": utc_now_iso(), "modelVersion": model_version})
        return bool(self.client.set(_key(exam_id, email_id), payload, nx=True, ex=self.ttl_seconds))
