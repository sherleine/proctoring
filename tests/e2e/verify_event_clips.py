"""Checks every saved event pair with ffmpeg.

    python tests/e2e/verify_event_clips.py data/e2e/events

For each event_<id>.webm / event_<id>_audio.wav:
  - WebM holds VP8 video + Opus audio, first video packet is a keyframe at t=0
  - real lengths of WebM and WAV match
  - the WAV is the same audio as the WebM's track, aligned (cross-correlation offset ~0)
  - pre-event footage: the clip starts with >= 1 s of quiet before speech (first word not cut)
Requires ffmpeg/ffprobe on PATH and numpy.
"""

import json
import subprocess
import sys
from pathlib import Path

import numpy as np

SR = 16000


def run(cmd: list[str]) -> str:
    return subprocess.run(cmd, capture_output=True, text=True, check=True).stdout


def packets(path: Path, stream: str) -> list[tuple[float, float, str]]:
    out = run(["ffprobe", "-v", "error", "-select_streams", stream, "-show_entries", "packet=pts_time,duration_time,flags", "-of", "csv=p=0", str(path)])
    rows = []
    for line in out.split():
        pts, dur, flags = (line.split(",") + ["", "", ""])[:3]
        rows.append((float(pts), float(dur) if dur not in ("", "N/A") else 0.0, flags))
    return rows


def decode(path: Path) -> np.ndarray:
    # aresample=async=1 honours packet timestamps: gaps in the track become silence instead of
    # being closed up, so the decoded audio sits on the file's real timeline.
    raw = subprocess.run(
        ["ffmpeg", "-v", "error", "-i", str(path), "-vn", "-af", "aresample=async=1:first_pts=0", "-ac", "1", "-ar", str(SR), "-f", "f32le", "-"],
        capture_output=True, check=True,
    ).stdout
    return np.frombuffer(raw, dtype=np.float32)


def lag_ms(a: np.ndarray, b: np.ndarray) -> tuple[float, float]:
    n = len(a) + len(b)
    c = np.fft.irfft(np.fft.rfft(a, n) * np.conj(np.fft.rfft(b, n)), n)
    k = int(np.argmax(c))
    k = k if k < n // 2 else k - n
    return k / SR * 1000, float(c.max() / (np.linalg.norm(a) * np.linalg.norm(b) + 1e-12))


def speech_onset(x: np.ndarray) -> float:
    """First 20 ms window within 25 dB of the loudest window."""
    w = SR // 50
    e = np.array([np.sqrt(np.mean(x[i : i + w] ** 2)) + 1e-9 for i in range(0, len(x) - w, w)])
    db = 20 * np.log10(e)
    return float(np.argmax(db > db.max() - 25) * w / SR)


def verify(webm: Path, wav: Path, min_pre_s: float = 1.0) -> dict:
    streams = {s["codec_type"]: s for s in json.loads(run(["ffprobe", "-v", "error", "-show_entries", "stream=codec_type,codec_name,sample_rate", "-of", "json", str(webm)]))["streams"]}
    vp, ap = packets(webm, "v"), packets(webm, "a")
    v_end = vp[-1][0] + vp[-1][1]
    wav_dur = float(json.loads(run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "json", str(wav)]))["format"]["duration"])
    a = decode(webm)  # first_pts=0 + async resampling places the audio at its real timestamps
    b = decode(wav)
    lag, corr = lag_ms(a, b)
    onset = speech_onset(b)
    checks = {
        "vp8+opus": streams.get("video", {}).get("codec_name") == "vp8" and streams.get("audio", {}).get("codec_name") == "opus",
        "starts with keyframe at 0": vp[0][0] == 0 and "K" in vp[0][2],
        "audio starts at ~0": 0 <= ap[0][0] < 0.05,
        "lengths match": abs(v_end - wav_dur) < 0.1,
        # Opus is lossy, so the peak correlation with the PCM is below 1; a clear peak at ~0 ms lag is the evidence.
        "same audio, aligned": corr > 0.5 and abs(lag) < 30,
        "pre-event audio before speech": onset >= min_pre_s,
    }
    return {"file": webm.name, "video_s": round(v_end, 3), "wav_s": round(wav_dur, 3), "lag_ms": round(lag, 1), "corr": round(corr, 3), "speech_onset_s": round(onset, 2), "checks": checks}


def main(folder: str) -> int:
    pairs = sorted(p for p in Path(folder).glob("event_*.webm"))
    if not pairs:
        print("no event clips found")
        return 1
    # events.json (written by the e2e test) lists cards newest first; the earliest event may
    # begin right after monitoring started, before the full pre-event buffer existed.
    listing = Path(folder) / "events.json"
    earliest = json.loads(listing.read_text())[-1]["video"] if listing.exists() else None
    failed = 0
    for webm in pairs:
        r = verify(webm, webm.with_name(webm.stem + "_audio.wav"), 0.0 if webm.name == earliest else 1.0)
        ok = all(r["checks"].values())
        failed += not ok
        bad = [k for k, v in r["checks"].items() if not v]
        print(f"{'PASS' if ok else 'FAIL'}  {r['file']}: video {r['video_s']} s, wav {r['wav_s']} s, lag {r['lag_ms']} ms (corr {r['corr']}), speech at {r['speech_onset_s']} s{'  FAILED: ' + ', '.join(bad) if bad else ''}")
    print(f"\n{len(pairs) - failed}/{len(pairs)} event clips verified")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
