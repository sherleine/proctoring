"""End-to-end demo: upload a WAV through the backend API and wait for the AI result.

    .venv\\Scripts\\python scripts\\send_sample_event.py [path\\to\\clip.wav] [--api http://localhost:5080]

Creates a session, uploads the WAV as one speech event (exactly as the browser will), then polls the event
until the backend reports a final status, and prints the transcript and findings. The WAV must be 16 kHz,
mono, 16-bit PCM (the default is tests/fixtures/speech_16k_mono.wav).
"""
import argparse
import hashlib
import json
import sys
import time
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

import httpx
import soundfile as sf

DEFAULT_WAV = Path(__file__).resolve().parents[1] / "tests" / "fixtures" / "speech_16k_mono.wav"
FINAL = {"COMPLETED", "FAILED", "DEAD_LETTERED"}


def iso(dt: datetime) -> str:
    return dt.strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def metadata_for(wav: bytes, path: Path) -> dict:
    info = sf.info(str(path))
    duration = round(info.frames / info.samplerate, 3)
    start = datetime.now(timezone.utc) - timedelta(seconds=duration + 1)
    pre = min(0.5, duration / 2)
    return {
        "schemaVersion": "1.0", "eventId": str(uuid.uuid4()), "eventType": "speech", "sequenceNumber": 0,
        "part": {"groupId": str(uuid.uuid4()), "index": 0, "isFinal": True, "overlapSeconds": 0},
        "startedAt": iso(start), "endedAt": iso(start + timedelta(seconds=duration)),
        "speechStartedAt": iso(start + timedelta(seconds=pre)), "durationSeconds": duration, "preEventSeconds": pre,
        "trigger": {"type": "vad", "model": "silero-vad-v5", "startThreshold": 0.5, "endThreshold": 0.35,
                    "maxProbability": 0.9, "speechSeconds": duration / 2, "noiseFloorDbfs": None},
        "audio": {"format": "wav", "codec": "pcm_s16le", "sampleRate": 16000, "channels": 1,
                  "sizeBytes": len(wav), "sha256": hashlib.sha256(wav).hexdigest()},
        "video": {"recordedLocally": False, "uploaded": False},
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("wav", nargs="?", type=Path, default=DEFAULT_WAV)
    parser.add_argument("--api", default="http://localhost:5080")
    parser.add_argument("--timeout", type=float, default=120)
    args = parser.parse_args()

    wav = args.wav.read_bytes()
    with httpx.Client(base_url=args.api, timeout=30) as api:
        session = api.post("/api/v1/sessions", json={"candidateId": "cand-demo", "examId": "exam-demo"}).raise_for_status().json()
        auth = {"Authorization": f"Bearer {session['sessionToken']}"}
        meta = metadata_for(wav, args.wav)
        files = {"metadata": (None, json.dumps(meta), "application/json"), "audio": ("clip.wav", wav, "audio/wav")}
        upload = api.post(f"/api/v1/sessions/{session['sessionId']}/events", headers=auth, files=files)
        print(f"upload: HTTP {upload.status_code} -> {upload.json().get('status') or upload.json()}")
        if upload.status_code not in (200, 202):
            return 1

        url = f"/api/v1/sessions/{session['sessionId']}/events/{meta['eventId']}"
        started, seen = time.time(), None
        while time.time() - started < args.timeout:
            event = api.get(url, headers=auth).raise_for_status().json()
            if event["status"] != seen:
                seen = event["status"]
                print(f"  {time.time() - started:6.1f} s  {seen}")
            if seen in FINAL:
                break
            time.sleep(0.5)
        else:
            print("timed out waiting for a final status (is the AI service running?)")
            return 1

    result = event.get("result") or {}
    if event["status"] == "COMPLETED":
        speech, text = result["speech"], result["transcription"]
        print(f"speech verified: {speech['verified']} ({speech['speechSeconds']} s, segments {speech['segments']})")
        print(f"language: {text.get('language')}  transcript: {text.get('text')!r}")
        for f in result["findings"]:
            print(f"finding: {f['type']} [{f['severity']}] {f['description']}")
        print(f"models: {result['models']}  processing: {result['processingMs']} ms")
        return 0
    print(f"error: {event.get('error')}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
