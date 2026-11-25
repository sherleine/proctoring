"""Phase 5: failure handling of the RUNNING system (backend + Redis + AI service started by scripts/run-all.ps1).

    python -m evaluation.e2e_resilience <LibriSpeech/test-clean> [--api http://localhost:5080]

Uses only the public HTTP API, Redis (to inspect streams / inject entries), docker pause, process stop/start through
run-all.ps1 and a READ-ONLY database lookup (to find one clip's stored file). No test hooks in the product code.

  1. AI service down      -> clips stay QUEUED, all processed after it restarts
  2. missing audio        -> that clip FAILED (AUDIO_NOT_FOUND); the clips before and after it COMPLETED
  3. duplicate delivery   -> no second result, no duplicate observation rows
  4. malformed message    -> dead-letter stream, worker keeps processing the next clip
  5. Redis paused         -> upload answered 503 + stored; published automatically after Redis is back
  6. burst of 10 clips    -> all COMPLETED once, live updates (SSE) for every clip, latency per clip
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import secrets
import sqlite3
import subprocess
import sys
import threading
import time
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

import httpx
import numpy as np
import redis
import soundfile as sf

ROOT = Path(__file__).resolve().parents[2]  # proctoring-platform
RUN_ALL = ROOT / "scripts" / "run-all.ps1"
DB = ROOT / "backend/src/Proctoring.Api/data/db/proctoring.db"
AUDIO = ROOT / "backend/src/Proctoring.Api/data/audio"
EVENTS, RESULTS, DLQ = "proctoring.audio.events.v1", "proctoring.audio.results.v1", "proctoring.audio.events.dlq.v1"
SR = 16_000
failures = 0
report: dict = {}


def check(ok: bool, text: str) -> None:
    global failures
    print(("PASS  " if ok else "FAIL  ") + text, flush=True)
    failures += 0 if ok else 1


def run_all(action: str, service: str) -> None:
    subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(RUN_ALL), action,
                    "-Service", service], check=True, capture_output=True, timeout=600)


def iso(dt: datetime) -> str:
    return dt.strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


class Client:
    def __init__(self, api: str, clips: list[np.ndarray]):
        self.http = httpx.Client(base_url=api, timeout=30)
        r = self.http.post("/api/v1/sessions", json={"candidateId": "cand-resilience", "examId": "exam-resilience"},
                           headers={"Idempotency-Key": secrets.token_hex(20)})
        r.raise_for_status()
        body = r.json()
        self.sid, self.token = body["sessionId"], body["sessionToken"]
        self.auth = {"Authorization": f"Bearer {self.token}"}
        self.t0 = datetime.now(timezone.utc)
        self.seq = 0
        self.clips = clips

    def upload(self) -> tuple[str, httpx.Response]:
        audio = self.clips[self.seq % len(self.clips)]
        buf = io.BytesIO()
        sf.write(buf, audio, SR, format="WAV", subtype="PCM_16")
        data = buf.getvalue()
        duration = round((len(data) - 44) / 32000, 3)
        start = self.t0 + timedelta(seconds=10 * self.seq)
        event_id = str(uuid.uuid4())
        metadata = {
            "schemaVersion": "1.0", "eventId": event_id, "eventType": "speech", "sequenceNumber": self.seq,
            "part": {"groupId": str(uuid.uuid4()), "index": 0, "isFinal": True, "overlapSeconds": 0},
            "startedAt": iso(start), "endedAt": iso(start + timedelta(seconds=duration)),
            "speechStartedAt": iso(start + timedelta(seconds=0.2)), "durationSeconds": duration, "preEventSeconds": 0.2,
            "trigger": {"type": "vad", "model": "silero-vad-v5", "startThreshold": 0.5, "endThreshold": 0.35,
                        "maxProbability": 0.99, "speechSeconds": duration, "noiseFloorDbfs": None},
            "audio": {"format": "wav", "codec": "pcm_s16le", "sampleRate": 16000, "channels": 1,
                      "sizeBytes": len(data), "sha256": hashlib.sha256(data).hexdigest()},
            "video": {"recordedLocally": False, "uploaded": False},
        }
        self.seq += 1
        r = self.http.post(f"/api/v1/sessions/{self.sid}/events", headers=self.auth,
                           files={"metadata": (None, json.dumps(metadata), "application/json"),
                                  "audio": ("clip.wav", data, "audio/wav")})
        return event_id, r

    def event(self, event_id: str) -> dict:
        return self.http.get(f"/api/v1/sessions/{self.sid}/events/{event_id}", headers=self.auth).json()

    def wait(self, event_ids: list[str], final=("COMPLETED", "FAILED", "DEAD_LETTERED"), timeout=180.0) -> dict[str, dict]:
        deadline, done = time.time() + timeout, {}
        while time.time() < deadline and len(done) < len(event_ids):
            for e in event_ids:
                if e not in done:
                    ev = self.event(e)
                    if ev["status"] in final:
                        done[e] = ev
            time.sleep(0.5)
        return done

    def observations(self) -> list[dict]:
        return self.http.get(f"/api/v1/sessions/{self.sid}/observations", headers=self.auth).json()["items"]


def completed_results(r: redis.Redis, event_id: str) -> int:
    return sum(1 for _, f in r.xrange(RESULTS) if f.get("eventId") == event_id
               and f.get("messageType") == "audio.analysis.completed")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("librispeech", type=Path)
    ap.add_argument("--api", default="http://localhost:5080")
    args = ap.parse_args()
    spk = sorted(p.name for p in args.librispeech.iterdir() if p.is_dir())[1]
    clips = []
    for f in sorted((args.librispeech / spk).rglob("*.flac"))[:12]:
        a, _ = sf.read(f, dtype="float32")
        clips.append(a[: 6 * SR])
    r = redis.Redis(host="localhost", port=6379, decode_responses=True)
    c = Client(args.api, clips)
    print(f"session {c.sid}")

    # 1. AI service down ----------------------------------------------------------------------------------
    run_all("stop", "ai")
    ids = [c.upload()[0] for _ in range(3)]
    time.sleep(3)
    statuses = [c.event(e)["status"] for e in ids]
    check(all(s == "QUEUED" for s in statuses), f"1. AI down: uploads accepted and QUEUED ({statuses})")
    t = time.time()
    run_all("start", "ai")
    done = c.wait(ids)
    check(len(done) == 3 and all(d["status"] == "COMPLETED" for d in done.values()),
          f"1. after the AI restarted all 3 queued clips COMPLETED ({time.time() - t:.1f} s incl. model loading)")

    # 2. missing audio -------------------------------------------------------------------------------------
    run_all("stop", "ai")
    ids = [c.upload()[0] for _ in range(3)]
    con = sqlite3.connect(f"file:{DB}?mode=ro", uri=True)
    key = con.execute("select storage_key from audio_events where session_id=? and event_id=?",
                      (c.sid, ids[1])).fetchone()
    if key is None:  # EF stores Guids as upper-case TEXT
        key = con.execute("select storage_key from audio_events where session_id=? and upper(event_id)=upper(?)",
                          (c.sid, ids[1])).fetchone()
    con.close()
    (AUDIO / key[0]).unlink()
    run_all("start", "ai")
    done = c.wait(ids)
    st = [done.get(e, {}).get("status") for e in ids]
    err = (done.get(ids[1], {}).get("error") or {}).get("code")
    check(st == ["COMPLETED", "FAILED", "COMPLETED"] and err == "AUDIO_NOT_FOUND",
          f"2. missing audio: clip 2 {st[1]} ({err}); clips 1 and 3 {st[0]} / {st[2]}")

    # 3. duplicate delivery ----------------------------------------------------------------------------------
    target = ids[0]
    entry = next(f for _, f in r.xrange(EVENTS) if f.get("eventId") == target)
    before_obs = len(c.observations())
    before_results = completed_results(r, target)
    before_updated = c.event(target)["updatedAt"]
    r.xadd(EVENTS, {**entry, "messageId": str(uuid.uuid4())})
    time.sleep(8)
    check(completed_results(r, target) == before_results == 1, "3. duplicate delivery: still exactly one completed result")
    check(len(c.observations()) == before_obs and c.event(target)["updatedAt"] == before_updated,
          f"3. duplicate delivery: no new observation rows ({before_obs}) and the event is unchanged")

    # 4. malformed message ----------------------------------------------------------------------------------
    dlq_before = r.xlen(DLQ)
    r.xadd(EVENTS, {"messageType": "garbage", "note": "resilience test"})
    eid, _ = c.upload()
    done = c.wait([eid], timeout=60)
    check(r.xlen(DLQ) == dlq_before + 1, f"4. malformed entry moved to the dead-letter stream ({dlq_before} -> {r.xlen(DLQ)})")
    check(done.get(eid, {}).get("status") == "COMPLETED", "4. the next clip was still processed")

    # 5. Redis paused -----------------------------------------------------------------------------------------
    subprocess.run(["docker", "pause", "proctoring-redis-1"], check=True, capture_output=True)
    try:
        eid, resp = c.upload()
        stored = resp.status_code == 503 and (resp.json().get("event") or {}).get("status") == "RECEIVED"
        check(stored, f"5. Redis paused: upload answered {resp.status_code}, event stored as RECEIVED")
    finally:
        subprocess.run(["docker", "unpause", "proctoring-redis-1"], check=True, capture_output=True)
    t = time.time()
    done = c.wait([eid], timeout=180)
    check(done.get(eid, {}).get("status") == "COMPLETED",
          f"5. after Redis came back the stored clip was published and COMPLETED ({time.time() - t:.1f} s; maintenance runs every 30 s)")

    # 6. burst of 10 clips with live updates -------------------------------------------------------------------
    seen: dict[str, list[str]] = {}
    stop = threading.Event()

    def listen():
        with httpx.Client(base_url=args.api, timeout=None) as h, \
                h.stream("GET", f"/api/v1/sessions/{c.sid}/updates", headers={**c.auth, "Accept": "text/event-stream"}) as s:
            for line in s.iter_lines():
                if stop.is_set():
                    break
                if line.startswith("data:"):
                    try:
                        d = json.loads(line[5:])
                    except json.JSONDecodeError:
                        continue
                    if isinstance(d, dict) and d.get("eventId"):
                        seen.setdefault(d["eventId"], []).append(d.get("status"))

    listener = threading.Thread(target=listen, daemon=True)
    listener.start()
    time.sleep(1)
    t = time.time()
    sent = {}
    for _ in range(10):
        eid, resp = c.upload()
        sent[eid] = time.time()
        check(resp.status_code == 202, f"6. burst upload {len(sent)} accepted ({resp.status_code})") if resp.status_code != 202 else None
    done = c.wait(list(sent), timeout=300)
    total = time.time() - t
    ok = len(done) == 10 and all(d["status"] == "COMPLETED" for d in done.values())
    check(ok, f"6. burst: {sum(d['status'] == 'COMPLETED' for d in done.values())}/10 COMPLETED in {total:.1f} s")
    time.sleep(2)
    stop.set()
    check(all("COMPLETED" in seen.get(e, []) for e in sent),
          f"6. live updates (SSE) delivered COMPLETED for {sum('COMPLETED' in seen.get(e, []) for e in sent)}/10 clips")
    check(all(completed_results(r, e) == 1 for e in sent), "6. exactly one completed result per clip in the results stream")
    report["burstSeconds"] = round(total, 1)
    report["burstClipsPerMinute"] = round(10 / total * 60, 1)
    rows = c.observations()
    report["observationRows"] = len(rows)
    report["duplicateObservationIds"] = len(rows) - len({o["observationId"] for o in rows})
    check(report["duplicateObservationIds"] == 0, f"6. no duplicate observation rows ({len(rows)} rows)")

    print(json.dumps(report))
    print("RESILIENCE", "PASSED" if failures == 0 else f"FAILED ({failures})")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
