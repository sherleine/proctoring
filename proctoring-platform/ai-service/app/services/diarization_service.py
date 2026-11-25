"""Speaker turns and overlapping speech inside one event clip (pyannote.audio 4.1.1, community-1, CPU).

Why only the SEGMENTATION step of pyannote/speaker-diarization-community-1:
the full pipeline ends with a clustering step that needs more speech per speaker than an event clip (<= 10 s)
contains. In the Phase 2 probe (docs/results/phase2-pyannote-benchmark.md, LibriSpeech calibration speakers)
the full pipeline found the second speaker of a turn-taking clip in 1/20 clips, while its segmentation model found it
in 17/20, and overlap in 18-20/20. So this service uses the pipeline's segmentation output (up to 3 local speakers
per 10 s window, with overlap) and leaves WHO each speaker is to the CAM++ comparison with the enrolled voice.

What it returns (all times in seconds inside the clip):
  * speakers: anonymous local labels S1, S2, ... ordered by first appearance - NOT identities; the first speaker is
    never assumed to be the candidate;
  * turns: (start, end, speaker), gaps shorter than `merge_gap_seconds` inside a speaker's speech are closed
    (natural pauses), pieces shorter than `min_turn_seconds` are dropped;
  * overlaps: regions where >= 2 speakers are active for at least `min_overlap_seconds`;
  * per speaker: total speech, exclusive (non-overlapping) speech and its level (for background-speech rules).
A speaker counts only with >= `min_speaker_seconds` of speech. Clips longer than one 10 s window are split into
windows by pyannote; local labels are per window (W1S1, W2S1, ...) and are linked later by the voice comparison.

Privacy: telemetry is disabled in app/__init__.py before pyannote is imported; the model is loaded from the local
cache with a pinned revision and works offline after the first download.
"""
from __future__ import annotations

import logging
import threading
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from app.core.errors import TransientEventError

log = logging.getLogger(__name__)
SAMPLE_RATE = 16_000


@dataclass(frozen=True)
class DiarizationConfig:
    model: str = "pyannote/speaker-diarization-community-1"
    revision: str = "3533c8cf8e369892e6b79ff1bf80f7b0286a54ee"
    cache_dir: Path | None = None
    offline: bool = False
    threads: int = 4
    activation: float = 0.5          # frame is "active" for a local speaker above this (binarized output: 0/1)
    min_speaker_seconds: float = 1.0  # a local speaker must be active this long in a window to count
    min_turn_seconds: float = 0.3
    merge_gap_seconds: float = 0.5   # pauses shorter than this inside one speaker's speech are closed
    min_overlap_seconds: float = 0.5


@dataclass
class SpeakerInfo:
    label: str
    speech_seconds: float
    exclusive_seconds: float
    rms_dbfs: float | None  # level of the speaker's exclusive speech (None if there is none)
    exclusive: list[tuple[float, float]] = field(default_factory=list)

    def to_dict(self) -> dict:
        return {"speaker": self.label, "speechSeconds": round(self.speech_seconds, 2),
                "exclusiveSeconds": round(self.exclusive_seconds, 2),
                "rmsDbfs": None if self.rms_dbfs is None else round(self.rms_dbfs, 1)}


@dataclass
class DiarizationResult:
    turns: list[tuple[float, float, str]]
    overlaps: list[tuple[float, float]]
    speakers: list[SpeakerInfo]

    @property
    def speaker_count(self) -> int:
        return len(self.speakers)

    @property
    def overlap_seconds(self) -> float:
        return round(sum(e - s for s, e in self.overlaps), 3)


class DiarizationService:
    def __init__(self, config: DiarizationConfig, token: str | None = None):
        self.config = config
        self._token = token
        self._pipeline = None
        self._lock = threading.Lock()
        self.model_id = f"{config.model.split('/')[-1]}@{config.revision[:8]}:segmentation"

    @property
    def loaded(self) -> bool:
        return self._pipeline is not None

    def load(self) -> None:
        import os

        if self.config.cache_dir is not None:
            os.environ.setdefault("HF_HOME", str(self.config.cache_dir))
        if self.config.offline:
            os.environ["HF_HUB_OFFLINE"] = "1"
        os.environ["PYANNOTE_METRICS_ENABLED"] = "false"  # again, in case app/__init__ was bypassed
        import torch
        from pyannote.audio import Pipeline

        torch.set_num_threads(max(1, self.config.threads))
        pipeline = Pipeline.from_pretrained(self.config.model, token=self._token, revision=self.config.revision)
        if pipeline is None:
            raise RuntimeError("diarization model could not be loaded (gated model: accept its conditions and set HF_TOKEN)")
        pipeline.to(torch.device("cpu"))
        self._pipeline = pipeline
        self._token = None  # not needed any more once loaded
        log.info("Diarization model loaded", extra={"model": self.config.model, "revision": self.config.revision,
                                                    "offline": self.config.offline, "device": "cpu"})

    # ------------------------------------------------------------------ inference

    def diarize(self, samples: np.ndarray) -> DiarizationResult:
        if self._pipeline is None:
            raise TransientEventError("diarization model is not loaded")
        import torch

        try:
            with self._lock, torch.inference_mode():
                seg = self._pipeline.get_segmentations(
                    {"waveform": torch.from_numpy(np.ascontiguousarray(samples, dtype=np.float32)).unsqueeze(0),
                     "sample_rate": SAMPLE_RATE, "uri": "clip"})
        except Exception as exc:  # inference failure: retried by the worker, never guessed
            raise TransientEventError(f"diarization failed ({type(exc).__name__})") from exc
        return self._interpret(seg.data, seg.sliding_window, len(samples) / SAMPLE_RATE, samples)

    def _interpret(self, data: np.ndarray, windows, duration: float, samples: np.ndarray) -> DiarizationResult:
        cfg = self.config
        n_chunks, n_frames, n_local = data.shape
        many = n_chunks > 1
        # Frame activity per (window, local speaker) on a common 10 ms grid over the clip.
        grid = 0.01
        n_grid = max(1, int(np.ceil(duration / grid)))
        activity: dict[str, np.ndarray] = {}
        for c in range(n_chunks):
            window = windows[c]
            frame_step = window.duration / n_frames
            for k in range(n_local):
                active = data[c, :, k] > cfg.activation
                if active.sum() * frame_step < cfg.min_speaker_seconds:
                    continue
                track = np.zeros(n_grid, dtype=bool)
                for f in np.flatnonzero(active):
                    t0 = window.start + f * frame_step
                    i0, i1 = int(t0 / grid), int(np.ceil((t0 + frame_step) / grid))
                    track[max(0, i0):min(n_grid, i1)] = True
                activity[f"W{c + 1}:{k}" if many else str(k)] = track

        # Pause handling: close short gaps, drop very short pieces.
        def segments(track: np.ndarray) -> list[tuple[float, float]]:
            out: list[list[float]] = []
            idx = np.flatnonzero(np.diff(np.concatenate([[0], track.astype(np.int8), [0]])))
            for s, e in zip(idx[0::2], idx[1::2]):
                start, end = s * grid, min(e * grid, duration)
                if out and start - out[-1][1] < cfg.merge_gap_seconds:
                    out[-1][1] = end
                else:
                    out.append([start, end])
            return [(round(s, 3), round(e, 3)) for s, e in out if e - s >= cfg.min_turn_seconds]

        per_speaker = {key: segments(track) for key, track in activity.items()}
        per_speaker = {k: v for k, v in per_speaker.items() if sum(e - s for s, e in v) >= cfg.min_speaker_seconds}
        # Anonymous labels in order of first appearance (never "candidate" by position).
        order = sorted(per_speaker, key=lambda k: per_speaker[k][0][0])
        names = {key: (f"W{key.split(':')[0][1:]}S{i + 1}" if many else f"S{i + 1}") for i, key in enumerate(order)}

        turns = sorted((s, e, names[k]) for k, segs in per_speaker.items() for s, e in segs)
        # Overlap: >= 2 speakers active at once (on the cleaned turns).
        count = np.zeros(n_grid, dtype=np.int8)
        for segs in per_speaker.values():
            for s, e in segs:
                count[int(s / grid):int(np.ceil(e / grid))] += 1
        overlaps = [(s, e) for s, e in segments(count >= 2) if e - s >= cfg.min_overlap_seconds]

        speakers = []
        overlap_mask = count >= 2
        for key in order:
            segs = per_speaker[key]
            mask = np.zeros(n_grid, dtype=bool)
            for s, e in segs:
                mask[int(s / grid):int(np.ceil(e / grid))] = True
            exclusive_mask = mask & ~overlap_mask
            exclusive = segments(exclusive_mask)
            pieces = [samples[int(s * SAMPLE_RATE):int(e * SAMPLE_RATE)] for s, e in exclusive]
            audio = np.concatenate(pieces) if pieces else np.zeros(0, np.float32)
            rms = float(np.sqrt(np.mean(np.square(audio, dtype=np.float64)))) if len(audio) else 0.0
            speakers.append(SpeakerInfo(
                label=names[key], speech_seconds=float(mask.sum() * grid),
                exclusive_seconds=float(sum(e - s for s, e in exclusive)),
                rms_dbfs=None if len(audio) == 0 else (-120.0 if rms <= 1e-6 else 20 * float(np.log10(rms))),
                exclusive=exclusive))
        return DiarizationResult(turns=turns, overlaps=overlaps, speakers=speakers)
