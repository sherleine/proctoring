"""End-to-end check of voice enrolment + comparison through the REAL running services
(backend HTTP API -> Redis Streams -> AI service with CAM++ -> backend -> HTTP), using real LibriSpeech voices.

    python -m evaluation.e2e_speaker_flow <LibriSpeech/test-clean dir> [--api http://localhost:5080]

Scenario (one exam session):
  1. voice sample of speaker A (~15 s)               -> enrolment accepted
  2. 5 s of speaker A (another chapter)              -> no observation
  3. 5 s of speaker B (same gender as A if possible) -> NON_CANDIDATE_VOICE
  4. 4 s of A + 1 s pause + 4 s of B                 -> MULTIPLE_SPEAKERS (+ NON_CANDIDATE_VOICE)
Prints PASS/FAIL per check and exits non-zero on any failure. Uses only the public HTTP API.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import secrets
import sys
import time
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

import httpx
import numpy as np
import soundfile as sf

SR = 16_000
failures = 0


def check(ok: bool, text: str) -> None:
    global failures
    print(("PASS  " if ok else "FAIL  ") + text, flush=True)
    failures += 0 if ok else 1


def iso(dt: datetime) -> str:
    return dt.strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def wav_bytes(x: np.ndarray) -> bytes:
    buf = io.BytesIO()
    sf.write(buf, np.clip(x, -1, 1), SR, format="WAV", subtype="PCM_16")
    return buf.getvalue()


def speech(files: list[Path], seconds: float) -> np.ndarray:
    parts, total = [], 0.0
    for f in files:
        a, sr = sf.read(f, dtype="float32")
        assert sr == SR
        parts.append(a)
        total += len(a) / SR
        if total >= seconds:
            break
    return np.concatenate(parts)[: int(seconds * SR)]


def upload(client: httpx.Client, sid: str, token: str, audio: np.ndarray, start: datetime, seq: int, event_type: str) -> str:
    data = wav_bytes(audio)
    duration = round((len(data) - 44) / 32000, 3)
    event_id = str(uuid.uuid4())
    metadata = {
        "schemaVersion": "1.0", "eventId": event_id, "eventType": event_type, "sequenceNumber": seq,
        "part": {"groupId": str(uuid.uuid4()), "index": 0, "isFinal": True, "overlapSeconds": 0},
        "startedAt": iso(start), "endedAt": iso(start + timedelta(seconds=duration)),
        "speechStartedAt": iso(start + timedelta(seconds=0.2)), "durationSeconds": duration,
        "preEventSeconds": 0 if event_type == "enrolment" else 0.2,
        "trigger": {"type": "enrolment" if event_type == "enrolment" else "vad", "model": "silero-vad-v5",
                    "startThreshold": 0.5, "endThreshold": 0.35, "maxProbability": 0.99,
                    "speechSeconds": duration, "noiseFloorDbfs": None},
        "audio": {"format": "wav", "codec": "pcm_s16le", "sampleRate": 16000, "channels": 1,
                  "sizeBytes": len(data), "sha256": hashlib.sha256(data).hexdigest()},
        "video": {"recordedLocally": False, "uploaded": False},
    }
    import json
    r = client.post(f"/api/v1/sessions/{sid}/events", headers={"Authorization": f"Bearer {token}"},
                    files={"metadata": (None, json.dumps(metadata), "application/json"),
                           "audio": ("clip.wav", data, "audio/wav")})
    check(r.status_code == 202, f"{event_type} upload accepted ({r.status_code})")
    return event_id


def wait_done(client: httpx.Client, sid: str, token: str, event_id: str, timeout: float = 60) -> dict:
    deadline = time.time() + timeout
    while time.time() < deadline:
        e = client.get(f"/api/v1/sessions/{sid}/events/{event_id}", headers={"Authorization": f"Bearer {token}"}).json()
        if e["status"] in ("COMPLETED", "FAILED", "DEAD_LETTERED"):
            return e
        time.sleep(0.5)
    raise TimeoutError(f"event {event_id} not processed in {timeout} s")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("librispeech", type=Path)
    ap.add_argument("--api", default="http://localhost:5080")
    args = ap.parse_args()

    root = args.librispeech
    gender = {}
    table = root.parent / "SPEAKERS.TXT"
    for line in table.read_text(encoding="utf-8", errors="ignore").splitlines():
        if not line.startswith(";") and "|" in line:
            p = [x.strip() for x in line.split("|")]
            gender[p[0]] = p[1]
    speakers = sorted(p.name for p in root.iterdir() if p.is_dir())
    a = speakers[1]  # an evaluation-split speaker (odd position): not used for threshold selection
    b = next(s for s in speakers[3::2] if gender.get(s) == gender.get(a))
    chapters_a = sorted((root / a).iterdir())
    enrol = speech(sorted(chapters_a[0].glob("*.flac")), 15)
    same = speech(sorted(chapters_a[-1].glob("*.flac"))[3:], 5)
    other = speech(sorted(next((root / b).iterdir()).glob("*.flac")), 5)
    mixed = np.concatenate([speech(sorted(chapters_a[-1].glob("*.flac"))[6:], 4), np.zeros(SR, np.float32),
                            speech(sorted(next((root / b).iterdir()).glob("*.flac"))[4:], 4)])
    print(f"speaker A = {a} ({gender.get(a)}), speaker B = {b} ({gender.get(b)})")

    with httpx.Client(base_url=args.api, timeout=30) as client:
        s = client.post("/api/v1/sessions", json={"candidateId": "cand-e2e", "examId": "exam-e2e"},
                        headers={"Idempotency-Key": secrets.token_hex(20)})
        check(s.status_code == 201, f"session created ({s.status_code})")
        body = s.json()
        sid, token = body["sessionId"], body["sessionToken"]
        t0 = datetime.now(timezone.utc)
        auth = {"Authorization": f"Bearer {token}"}

        enrol_id = upload(client, sid, token, enrol, t0, 0, "enrolment")
        started = time.perf_counter()
        e = wait_done(client, sid, token, enrol_id)
        enrolment = (e.get("result") or {}).get("enrolment") or {}
        check(e["status"] == "COMPLETED" and enrolment.get("status") == "accepted",
              f"voice sample accepted: {enrolment.get('status')} ({enrolment.get('speechSeconds')} s speech, "
              f"processed in {time.perf_counter() - started:.1f} s)")
        check(enrolment.get("embedding") is None and enrolment.get("embeddingStored") is True,
              "embedding is not in the result returned over HTTP")
        st = client.get(f"/api/v1/sessions/{sid}/enrolment", headers=auth)
        check(st.status_code == 200 and st.json().get("usedForComparison") is True, f"GET enrolment: {st.json()}")

        cases = [("same speaker A (5 s)", same, set()),
                 ("other speaker B (5 s)", other, {"NON_CANDIDATE_VOICE"}),
                 ("A then B in one clip", mixed, {"MULTIPLE_SPEAKERS_DETECTED", "NON_CANDIDATE_VOICE"})]
        for i, (name, audio, expected) in enumerate(cases, start=1):
            eid = upload(client, sid, token, audio, t0 + timedelta(seconds=20 * i), i, "speech")
            started = time.perf_counter()
            ev = wait_done(client, sid, token, eid)
            result = ev.get("result") or {}
            kinds = {o["observationType"] for o in result.get("observations", []) if o.get("category") != "informational"}
            info = sorted(o["observationType"] for o in result.get("observations", []) if o.get("category") == "informational")
            check(info == ["SPEECH_DETECTED"], f"{name}: informational record {info}")
            comps = [(c["start"], c["end"], c["similarity"], c["decision"]) for c in (result.get("speaker") or {}).get("comparisons", [])]
            check(kinds == expected, f"{name}: observations {sorted(kinds) or 'none'} (expected {sorted(expected) or 'none'}); "
                                     f"comparisons {comps}; {result.get('processingMs')} ms in AI, "
                                     f"{time.perf_counter() - started:.1f} s end to end")

        obs = client.get(f"/api/v1/sessions/{sid}/observations", headers=auth).json()
        review_rows = [o for o in obs["items"] if o.get("category") != "informational"]
        check(len(review_rows) == sum(len(c[2]) for c in cases), f"GET observations returned {len(review_rows)} review rows "
                                                                 f"(+{len(obs['items']) - len(review_rows)} informational)")
    print("E2E SPEAKER FLOW", "PASSED" if failures == 0 else f"FAILED ({failures})")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
