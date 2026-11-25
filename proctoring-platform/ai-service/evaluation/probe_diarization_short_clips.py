"""Probe: how many speakers does pyannote community-1 find in 10 s clips (our maximum event length)?

    .venv-diarization\\Scripts\\python -m evaluation.probe_diarization_short_clips <LibriSpeech/test-clean>

Uses only CALIBRATION-split speakers (even positions of the sorted speaker list; the odd positions are kept for the
final evaluation). For each clip it records (a) the number of speakers in the final pipeline output (after
clustering) and (b) the number of LOCAL speakers the segmentation model sees (active >= 1.0 s), captured through the
pipeline's documented `hook` argument - pyannote itself is not modified.
Clip types (10 s, 16 kHz, real LibriSpeech speech):
  single  - one speaker                                  (expected 1)
  turns   - A 4.5 s, 0.5 s pause, B 5 s                  (expected 2)
  overlap - A 10 s with B added at 3-7 s (gain 0.9)      (expected 2, ~4 s overlap)
Results -> evaluation/results/probe_diarization_short_clips.json
"""
from __future__ import annotations

import json
import os
import sys
from pathlib import Path

os.environ["PYANNOTE_METRICS_ENABLED"] = "false"  # no telemetry (see bench_diarization.py)

import numpy as np  # noqa: E402
import soundfile as sf  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
SR = 16_000
MODEL = "pyannote/speaker-diarization-community-1"


def main() -> None:
    libri = Path(sys.argv[1])
    env = dict(l.split("=", 1) for l in (ROOT / ".env").read_text(encoding="utf-8").splitlines() if "=" in l and not l.startswith("#"))
    os.environ["HF_HOME"] = str((ROOT / env.get("HF_HOME", "models/hf")).resolve())
    os.environ["HF_HUB_OFFLINE"] = "1"
    snap = next((Path(os.environ["HF_HOME"]) / "hub").glob("models--pyannote--speaker-diarization-community-1/snapshots/*"))

    import torch
    torch.set_num_threads(4)
    from pyannote.audio import Pipeline
    pipeline = Pipeline.from_pretrained(MODEL, revision=snap.name)

    gender = {}
    for line in (libri.parent / "SPEAKERS.TXT").read_text(encoding="utf-8", errors="ignore").splitlines():
        if "|" in line and not line.startswith(";"):
            p = [x.strip() for x in line.split("|")]
            gender[p[0]] = p[1]
    speakers = sorted(p.name for p in libri.iterdir() if p.is_dir())[0::2]  # calibration split only

    def audio(spk: str, skip: int, seconds: float) -> np.ndarray:
        out, total = [], 0
        for f in sorted((libri / spk).rglob("*.flac"))[skip:]:
            a, _ = sf.read(f, dtype="float32")
            out.append(a)
            total += len(a)
            if total >= seconds * SR:
                break
        return np.concatenate(out)[: int(seconds * SR)]

    def run(x: np.ndarray) -> dict:
        captured = {}

        def hook(step, artefact, file=None, **_):
            if step == "segmentation":
                captured["seg"] = artefact

        out = pipeline({"waveform": torch.from_numpy(x).unsqueeze(0), "sample_rate": SR}, hook=hook)
        diar = out.speaker_diarization
        local = 0
        seg = captured.get("seg")
        if seg is not None:
            frame = seg.sliding_window.step
            data = seg.data  # (chunks, frames, local speakers), powerset already converted to multi-label
            local = int(max(((chunk > 0.5).sum(axis=0) * frame >= 1.0).sum() for chunk in data))
        return {"pipeline": len(diar.labels()), "local": local,
                "overlap": round(sum(s.duration for s in diar.get_overlap()), 2)}

    rows = []
    n = len(speakers)
    for i, a in enumerate(speakers):
        b = speakers[(i + 1) % n]
        pair = f"{a}{gender.get(a, '?')}+{b}{gender.get(b, '?')}"
        single = run(audio(a, 0, 10))
        turns = run(np.concatenate([audio(a, 2, 4.5), np.zeros(SR // 2, np.float32), audio(b, 0, 5)]))
        mix = audio(a, 4, 10).copy()
        mix[3 * SR:7 * SR] += 0.9 * audio(b, 2, 4)
        overlap = run(np.clip(mix, -1, 1))
        rows.append({"speaker": a, "pair": pair, "sameGender": gender.get(a) == gender.get(b),
                     "single": single, "turns": turns, "overlap": overlap})
        print(f"{pair:14} single p{single['pipeline']}/l{single['local']}  turns p{turns['pipeline']}/l{turns['local']}  "
              f"overlap p{overlap['pipeline']}/l{overlap['local']} ov={overlap['overlap']}s", flush=True)

    def rate(kind, key, expected):
        return sum(r[kind][key] == expected for r in rows) / len(rows)

    summary = {
        "clips": len(rows),
        "single_pipeline_says_1": rate("single", "pipeline", 1), "single_local_says_1": rate("single", "local", 1),
        "turns_pipeline_says_2": rate("turns", "pipeline", 2), "turns_local_says_2": rate("turns", "local", 2),
        "overlap_pipeline_says_2": rate("overlap", "pipeline", 2), "overlap_local_says_2": rate("overlap", "local", 2),
        "overlap_seconds_median": float(np.median([r["overlap"]["overlap"] for r in rows])),
        "turns_pipeline_says_2_sameGender": np.mean([r["turns"]["pipeline"] == 2 for r in rows if r["sameGender"]]).item()
        if any(r["sameGender"] for r in rows) else None,
        "turns_pipeline_says_2_crossGender": np.mean([r["turns"]["pipeline"] == 2 for r in rows if not r["sameGender"]]).item()
        if any(not r["sameGender"] for r in rows) else None,
    }
    out = ROOT / "evaluation" / "results" / "probe_diarization_short_clips.json"
    out.write_text(json.dumps({"summary": summary, "rows": rows}, indent=2), encoding="utf-8")
    print(json.dumps(summary, indent=1))


if __name__ == "__main__":
    main()
