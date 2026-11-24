"""Calibrate and evaluate candidate voice comparison (CAM++) on LibriSpeech test-clean (real human read speech).

    python -m evaluation.speaker_verification <LibriSpeech/test-clean dir> [--out results.json]

Protocol (deterministic, no random choices except a fixed-seed noise generator):
  * 40 speakers sorted by id; even positions -> CALIBRATION split, odd positions -> EVALUATION split, so no voice
    is used for both threshold selection and the final numbers.
  * enrolment per speaker: utterances of the speaker's FIRST chapter, concatenated until >= 15 s, passed through
    the real enrolment code (Silero VAD + quality checks + window averaging).
  * test segments: up to 10 utterances from the speaker's OTHER chapters (different recording session), each cut
    to 1.5 s, 3 s and 5 s after its first VAD speech onset.
  * conditions: `clean`, and `degraded` = level -12 dB (quieter / different volume) + white noise at 15 dB SNR.
  * target trials: segment vs own enrolment; non-target trials: segment vs every other enrolment of the same split.
  * thresholds from the calibration split only:
        different = 1st percentile of target scores   (<= 1 % of genuine segments called LIKELY_NON_CANDIDATE)
        same      = 99th percentile of non-target scores (<= 1 % of other voices called LIKELY_CANDIDATE)
    then reported, unchanged, on the evaluation split.
LibriSpeech is clean audiobook speech; laptop-microphone rooms are harder. Numbers here are an upper bound.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

import numpy as np
import soundfile as sf

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from app.services.speaker_service import EnrolmentConfig, SpeakerService, SpeakerThresholds  # noqa: E402
from app.services.vad_service import VadService  # noqa: E402

SR = 16_000
LENGTHS = (1.5, 3.0, 5.0)
ENROL_SECONDS = 15.0
SEGMENTS_PER_SPEAKER = 10


def load_speakers(root: Path) -> dict[str, dict[str, list[Path]]]:
    speakers: dict[str, dict[str, list[Path]]] = {}
    for spk in sorted(p for p in root.iterdir() if p.is_dir()):
        chapters = {c.name: sorted(c.glob("*.flac")) for c in sorted(spk.iterdir()) if c.is_dir()}
        speakers[spk.name] = {k: v for k, v in chapters.items() if v}
    return speakers


def genders(root: Path) -> dict[str, str]:
    table = root.parent / "SPEAKERS.TXT"
    out = {}
    if table.exists():
        for line in table.read_text(encoding="utf-8", errors="ignore").splitlines():
            if line.startswith(";"):
                continue
            parts = [p.strip() for p in line.split("|")]
            if len(parts) >= 2:
                out[parts[0]] = parts[1]
    return out


def read(path: Path) -> np.ndarray:
    audio, sr = sf.read(path, dtype="float32")
    assert sr == SR and audio.ndim == 1
    return audio


def degrade(x: np.ndarray, rng: np.random.Generator) -> np.ndarray:
    quiet = x * 10 ** (-12 / 20)
    power = float(np.mean(quiet ** 2)) or 1e-12
    noise = rng.normal(0.0, np.sqrt(power / 10 ** (15 / 10)), size=x.shape).astype(np.float32)
    return np.clip(quiet + noise, -1.0, 1.0).astype(np.float32)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("librispeech", type=Path)
    ap.add_argument("--out", type=Path, default=ROOT / "evaluation" / "results" / "speaker_verification.json")
    args = ap.parse_args()

    vad = VadService(0.5)
    vad.load()
    speaker = SpeakerService(ROOT / "models/speaker/3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx",
                             SpeakerThresholds(0.99, -0.99), EnrolmentConfig())
    speaker.load()
    rng = np.random.default_rng(20261124)
    data = load_speakers(args.librispeech)
    gender = genders(args.librispeech)
    ids = list(data)
    split = {sid: ("calibration" if i % 2 == 0 else "evaluation") for i, sid in enumerate(ids)}

    enrolments, segments, embed_times = {}, [], []
    t_start = time.perf_counter()
    for sid in ids:
        chapters = list(data[sid])
        first, others = chapters[0], chapters[1:] or chapters[:1]
        # enrolment
        parts, total = [], 0.0
        for f in data[sid][first]:
            a = read(f)
            parts.append(a)
            total += len(a) / SR
            if total >= ENROL_SECONDS:
                break
        enrol_audio = np.concatenate(parts)
        segs = vad.detect(enrol_audio).segments
        result = speaker.enrol(enrol_audio, segs)
        enrolments[sid] = {"status": result.status, "speech": result.speech_seconds,
                           "consistency": result.self_consistency,
                           "embedding": None if result.embedding is None else np.asarray(result.embedding, np.float32)}
        # test segments from the other chapters
        files = [f for c in others for f in data[sid][c]]
        if others == chapters[:1]:
            files = files[len(parts):]  # same chapter: skip utterances used for enrolment
        used = 0
        for f in files:
            if used >= SEGMENTS_PER_SPEAKER:
                break
            a = read(f)
            vs = vad.detect(a).segments
            if not vs:
                continue
            onset = vs[0][0]
            if len(a) / SR - onset < max(LENGTHS):
                continue
            used += 1
            for length in LENGTHS:
                piece = a[int(onset * SR):int((onset + length) * SR)]
                for condition, audio in (("clean", piece), ("degraded", degrade(piece, rng))):
                    t = time.perf_counter()
                    emb = speaker.embed(audio)
                    embed_times.append((time.perf_counter() - t, length))
                    segments.append({"speaker": sid, "length": length, "condition": condition, "emb": emb})
        print(f"{sid} ({split[sid]}, {gender.get(sid, '?')}): enrolment {result.status} "
              f"{result.speech_seconds:.1f} s speech, consistency {result.self_consistency}; {used} test utterances",
              flush=True)

    # score every segment against every enrolment of the same split
    trials = []
    for seg in segments:
        for sid, enr in enrolments.items():
            if split[sid] != split[seg["speaker"]] or enr["embedding"] is None:
                continue
            trials.append({"split": split[sid], "target": sid == seg["speaker"], "length": seg["length"],
                           "condition": seg["condition"],
                           "sameGender": gender.get(sid) == gender.get(seg["speaker"]),
                           "score": float(seg["emb"] @ enr["embedding"])})

    def scores(split_name, target, **f):
        return np.array([t["score"] for t in trials if t["split"] == split_name and t["target"] == target
                         and all(t[k] == v for k, v in f.items())])

    cal_t, cal_n = scores("calibration", True), scores("calibration", False)
    different = float(np.percentile(cal_t, 1))
    same = float(np.percentile(cal_n, 99))
    note = None
    if different >= same:  # distributions separate: keep an uncertain band around the middle
        mid = (different + same) / 2
        same, different, note = max(same, mid + 0.02), min(different, mid - 0.02), "separated; band around midpoint"

    def outcome(arr):
        n = len(arr)
        return {"n": n,
                "likelyCandidate": round(float(np.mean(arr >= same)), 4) if n else None,
                "uncertain": round(float(np.mean((arr > different) & (arr < same))), 4) if n else None,
                "likelyNonCandidate": round(float(np.mean(arr <= different)), 4) if n else None}

    report = {"thresholds": {"same": round(same, 4), "different": round(different, 4), "note": note},
              "speakers": {"calibration": sum(v == "calibration" for v in split.values()),
                           "evaluation": sum(v == "evaluation" for v in split.values())},
              "enrolments": {s: {k: (round(v, 3) if isinstance(v, float) else v) for k, v in e.items() if k != "embedding"}
                             for s, e in enrolments.items()},
              "evaluation": {}, "calibration": {}, "timing": {}}
    for split_name in ("calibration", "evaluation"):
        for condition in ("clean", "degraded"):
            for length in LENGTHS:
                key = f"{condition}/{length}s"
                report[split_name][key] = {
                    "target": outcome(scores(split_name, True, condition=condition, length=length)),
                    "nonTarget": outcome(scores(split_name, False, condition=condition, length=length)),
                    "nonTargetSameGender": outcome(scores(split_name, False, condition=condition, length=length, sameGender=True)),
                }
    for length in LENGTHS:
        ts = [t for t, l in embed_times if l == length]
        report["timing"][f"{length}s"] = {"medianMs": round(float(np.median(ts)) * 1000, 2), "n": len(ts)}
    report["timing"]["totalSeconds"] = round(time.perf_counter() - t_start, 1)
    report["scoreSummary"] = {
        "calibrationTarget": {"p1": round(float(np.percentile(cal_t, 1)), 4), "median": round(float(np.median(cal_t)), 4)},
        "calibrationNonTarget": {"p99": round(float(np.percentile(cal_n, 99)), 4), "median": round(float(np.median(cal_n)), 4)},
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({k: report[k] for k in ("thresholds", "speakers", "evaluation", "timing", "scoreSummary")}, indent=1))


if __name__ == "__main__":
    main()
