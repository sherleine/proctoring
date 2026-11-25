"""Phase 3 evaluation: the service's real pipeline (Silero VAD -> pyannote segmentation -> CAM++ per speaker -> rules)
on LibriSpeech test-clean EVALUATION-split speakers (odd positions; never used for thresholds or the probe).

    .venv\\Scripts\\python -m evaluation.eval_diarization <LibriSpeech/test-clean>

SYNTHETIC MIXTURES of real read speech (no room acoustics, no laptop microphone): results are an upper bound for
real exam rooms. Per speaker A (paired with the next evaluation speaker B), 10 s clips:
  single      A only                                           truth: 1 speaker
  turns       A 4.5 s | 0.5 s pause | B 5 s                    truth: 2, sequential
  pausesplit  A 1.4 s | 0.8 s | A 1.2 s | 1.0 s | B 3.6 s      truth: 2 (A's speech cut by pauses)
  overlap     A 10 s + B at 3-7 s (gain 0.9)                   truth: 2, 4 s overlap
  background  A 10 s + B at 2-9 s, 20 dB quieter               truth: 2 (quiet background voice)
No voice sample is used: speakers are anonymous (the current configuration).
Ground-truth speech of each source piece = Silero VAD on that piece alone.
Metrics: speaker count (diarization) and distinct voices (with CAM++), DER (pyannote.metrics, collar 0 and 0.25 s,
overlap scored), overlap precision/recall on a 10 ms grid, voice roles, review observations, processing time.
Results -> evaluation/results/eval_diarization.json
"""
from __future__ import annotations

import json
import statistics
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

import numpy as np  # noqa: E402
import soundfile as sf  # noqa: E402

from app.core.config import Settings  # noqa: E402
from app.main import Runtime  # noqa: E402  (same construction as the service)
from app.schemas.audio_event import parse_event_entry  # noqa: E402
from tests.helpers import event_entry, new_session_id, put_audio, wav_bytes  # noqa: E402

SR = 16_000
GRID = 0.01


def main() -> None:
    libri = Path(sys.argv[1])
    settings = Settings()
    runtime = Runtime(settings)  # builds VAD, CAM++, pyannote exactly like the service (loaded below)
    runtime.vad.load()
    runtime.speaker.load()
    runtime.diarizer.load()
    vad, speaker = runtime.vad, runtime.speaker

    from pyannote.core import Annotation, Segment
    from pyannote.metrics.diarization import DiarizationErrorRate

    speakers = sorted(p.name for p in libri.iterdir() if p.is_dir())[1::2]  # evaluation split

    def files(spk, chapter_index):
        chapters = sorted(p for p in (libri / spk).iterdir() if p.is_dir())
        return sorted(chapters[chapter_index % len(chapters)].glob("*.flac"))

    def take(spk, chapter_index, skip, seconds):
        out, total = [], 0
        for f in files(spk, chapter_index)[skip:]:
            a, _ = sf.read(f, dtype="float32")
            out.append(a)
            total += len(a)
            if total >= seconds * SR:
                break
        return np.concatenate(out)[: int(seconds * SR)]

    def speech_spans(piece, offset):
        return [(offset + s, offset + e) for s, e in vad.detect(piece).segments]

    def build(kind, a, b):
        """Returns clip, truth {label: [(s,e)]}."""
        if kind == "single":
            x = take(a, 1, 0, 10)
            return x, {"A": speech_spans(x, 0)}
        if kind == "turns":
            pa, pb = take(a, 1, 2, 4.5), take(b, 1, 0, 5)
            return np.concatenate([pa, np.zeros(SR // 2, np.float32), pb]), {"A": speech_spans(pa, 0), "B": speech_spans(pb, 5.0)}
        if kind == "pausesplit":
            p1, p2, pb = take(a, 1, 4, 1.4), take(a, 1, 5, 1.2), take(b, 1, 2, 3.6)
            x = np.concatenate([p1, np.zeros(int(0.8 * SR), np.float32), p2, np.zeros(SR, np.float32), pb])
            return x, {"A": speech_spans(p1, 0) + speech_spans(p2, 2.2), "B": speech_spans(pb, 4.4)}
        if kind == "overlap":
            pa, pb = take(a, 1, 6, 10), take(b, 1, 3, 4)
            x = pa.copy()
            x[3 * SR:7 * SR] += 0.9 * pb
            return np.clip(x, -1, 1), {"A": speech_spans(pa, 0), "B": speech_spans(pb, 3.0)}
        if kind in ("bgalone10", "bgalone20"):  # quiet voice while the main speaker is silent (e.g. reading)
            pa, pb = take(a, 1, 10, 4.5), take(b, 1, 6, 5)
            pb = pb * 10 ** ((-10 if kind == "bgalone10" else -20) / 20)
            return np.concatenate([pa, np.zeros(SR // 2, np.float32), pb]), {"A": speech_spans(pa, 0), "B": speech_spans(pb / max(np.abs(pb).max(), 1e-6), 5.0)}
        if kind in ("background", "background10"):
            pa, pb = take(a, 1, 8, 10), take(b, 1, 4, 7)
            x = pa.copy()
            x[2 * SR:9 * SR] += pb * 10 ** ((-20 if kind == "background" else -10) / 20)
            return np.clip(x, -1, 1), {"A": speech_spans(pa, 0), "B": speech_spans(pb, 2.0)}
        raise ValueError(kind)

    def grid_mask(spans, n):
        m = np.zeros(n, dtype=bool)
        for s, e in spans:
            m[int(s / GRID):int(np.ceil(e / GRID))] = True
        return m

    der0, der25 = DiarizationErrorRate(collar=0.0, skip_overlap=False), DiarizationErrorRate(collar=0.25, skip_overlap=False)
    kinds = ["single", "turns", "pausesplit", "overlap", "background10", "background", "bgalone10", "bgalone20"]
    rows = []
    from app.services.audio_loader import AudioStore
    from app.services.processing_pipeline import ProcessingPipeline
    with tempfile.TemporaryDirectory() as tmp:
        store = Path(tmp)
        # same loaded models and settings as the service, reading clips from this temporary store
        pipeline = ProcessingPipeline(AudioStore(store), vad, None, speaker, runtime.diarizer,
                                      settings.diarization_min_speech_seconds)
        for i, a in enumerate(speakers):
            b = speakers[(i + 1) % len(speakers)]
            sid = new_session_id()
            for kind in kinds:
                clip, truth = build(kind, a, b)
                data = wav_bytes(clip)
                _, key = put_audio(store, data, session_id=sid)
                entry = event_entry(sid, key, data, duration=round(len(clip) / SR, 3))
                t0 = time.perf_counter()
                payload = pipeline.process(parse_event_entry(entry)).to_payload()
                elapsed = time.perf_counter() - t0
                d = payload["diarization"]
                hyp = Annotation()
                for t in d["turns"]:
                    hyp[Segment(t["start"], t["end"])] = t["speaker"]
                ref = Annotation()
                for label, spans in truth.items():
                    for s, e in spans:
                        ref[Segment(s, e)] = label
                der_0 = der0(ref, hyp)
                der_25 = der25(ref, hyp)
                n = int(np.ceil(len(clip) / SR / GRID))
                truth_ov = grid_mask(truth.get("A", []), n) & grid_mask(truth.get("B", []), n)
                hyp_ov = grid_mask([(o["start"], o["end"]) for o in d["overlaps"]], n)
                review = sorted(o["observationType"] for o in payload["observations"] if o["category"] == "review")
                roles = {s["speaker"]: s.get("voice") for s in d.get("speakers", [])}
                rows.append({"speaker": a, "other": b, "kind": kind, "truthSpeakers": len(truth),
                             "diarizedSpeakers": d.get("speakerCount"), "distinctVoices": d.get("distinctVoices"),
                             "roles": roles, "review": review, "der0": round(der_0, 4), "der25": round(der_25, 4),
                             "truthOverlap": round(truth_ov.sum() * GRID, 2), "hypOverlap": round(hyp_ov.sum() * GRID, 2),
                             "overlapTP": round((truth_ov & hyp_ov).sum() * GRID, 2), "seconds": round(elapsed, 3)})
                print(f"{a:>5}+{b:<5} {kind:10} diar={d.get('speakerCount')} voices={d.get('distinctVoices')} "
                      f"roles={list(roles.values())} review={review} DER={der_0:.3f} ov={rows[-1]['hypOverlap']}/{rows[-1]['truthOverlap']}s "
                      f"{elapsed * 1000:.0f} ms", flush=True)

    summary = {"speakers": len(speakers), "clipsPerKind": len(speakers),
               "totalDER_collar0": round(abs(der0), 4), "totalDER_collar0.25": round(abs(der25), 4)}
    for kind in kinds:
        k = [r for r in rows if r["kind"] == kind]
        expected = 1 if kind == "single" else 2
        tp = sum(r["overlapTP"] for r in k)
        hyp = sum(r["hypOverlap"] for r in k)
        tru = sum(r["truthOverlap"] for r in k)
        summary[kind] = {
            "diarizedSpeakersCorrect": round(np.mean([r["diarizedSpeakers"] == expected for r in k]), 3),
            "distinctVoicesCorrect": round(np.mean([r["distinctVoices"] == expected for r in k]), 3),
            "multipleSpeakersObservation": round(np.mean(["MULTIPLE_SPEAKERS_DETECTED" in r["review"] for r in k]), 3),
            "overlappingSpeechObservation": round(np.mean(["OVERLAPPING_SPEECH_DETECTED" in r["review"] for r in k]), 3),
            "backgroundObservation": round(np.mean(["BACKGROUND_CONVERSATION_DETECTED" in r["review"] for r in k]), 3),
            "assistanceObservation": round(np.mean(["POSSIBLE_EXTERNAL_ASSISTANCE" in r["review"] for r in k]), 3),
            "anyReviewObservation": round(np.mean([bool(r["review"]) for r in k]), 3),
            "meanDER_collar0": round(float(np.mean([r["der0"] for r in k])), 4),
            "overlapPrecision": round(tp / hyp, 3) if hyp else None,
            "overlapRecall": round(tp / tru, 3) if tru else None,
            "medianSeconds": round(statistics.median(r["seconds"] for r in k), 3),
        }
    out = ROOT / "evaluation" / "results" / "eval_diarization.json"
    out.write_text(json.dumps({"summary": summary, "rows": rows}, indent=2), encoding="utf-8")
    print(json.dumps(summary, indent=1))


if __name__ == "__main__":
    main()
