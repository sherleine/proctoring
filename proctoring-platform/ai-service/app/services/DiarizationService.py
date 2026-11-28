"""pyannote speaker segmentation -> anonymous speaker turns, overlaps and each speaker's exclusive speech."""
from __future__ import annotations

import logging
import os
import threading
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from app.core.errors import TransientEventError
from app.utils.audio_utils import SAMPLE_RATE, slice_spans

log = logging.getLogger(__name__)


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
    link_agreement: float = 0.5       # stitch two windows' local speakers when their shared-time activity agrees this much (IoU)
    min_link_seconds: float = 0.3     # ... and both are active for at least this long in the shared time


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
        if self.config.cache_dir is not None:
            os.environ.setdefault("HF_HOME", str(self.config.cache_dir))
        if self.config.offline:
            os.environ["HF_HUB_OFFLINE"] = "1"
        import torch
        from pyannote.audio import Pipeline

        torch.set_num_threads(max(1, self.config.threads))
        pipeline = Pipeline.from_pretrained(self.config.model, token=self._token, revision=self.config.revision)
        if pipeline is None:
            raise RuntimeError("diarization model could not be loaded (gated model: accept its conditions and set HF_TOKEN)")
        pipeline.to(torch.device("cpu"))
        self._pipeline = pipeline
        self._token = None
        log.info("Diarization model loaded", extra={"model": self.config.model, "revision": self.config.revision,
                                                    "offline": self.config.offline, "device": "cpu"})

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
        # Frame activity per (window, local speaker) on a common 10 ms grid over the clip.
        grid = 0.01
        n_grid = max(1, int(np.ceil(duration / grid)))

        def to_grid(active: np.ndarray, window) -> np.ndarray:
            frame_step = window.duration / n_frames
            track = np.zeros(n_grid, dtype=bool)
            for f in np.flatnonzero(active):
                t0 = window.start + f * frame_step
                i0, i1 = int(t0 / grid), int(np.ceil((t0 + frame_step) / grid))
                track[max(0, i0):min(n_grid, i1)] = True
            return track

        local: dict[tuple[int, int], np.ndarray] = {}   # (window, local speaker) -> grid track
        coverage = np.zeros(n_grid, dtype=np.int16)      # how many windows cover each grid frame
        overlap_votes = np.zeros(n_grid, dtype=np.int16)  # ... and how many of them see >= 2 speakers at once
        spans: list[tuple[int, int]] = []
        for c in range(n_chunks):
            window = windows[c]
            frame_step = window.duration / n_frames
            g0 = max(0, int(window.start / grid))
            g1 = min(n_grid, int(np.ceil((window.start + window.duration) / grid)))
            spans.append((g0, g1))
            coverage[g0:g1] += 1
            kept = []
            for k in range(n_local):
                active = data[c, :, k] > cfg.activation
                if active.sum() * frame_step < cfg.min_speaker_seconds:
                    continue
                local[(c, k)] = to_grid(active, window)
                kept.append((c, k))
            if len(kept) >= 2:
                overlap_votes += (np.sum([local[key] for key in kept], axis=0) >= 2).astype(np.int16)

        # Stitch local speakers across overlapping windows (union-find; at most one local speaker per window per group).
        parent = {key: key for key in local}
        members = {key: {key[0]} for key in local}

        def find(key):
            while parent[key] != key:
                parent[key] = parent[parent[key]]
                key = parent[key]
            return key

        min_link_frames = cfg.min_link_seconds / grid
        links = []
        keys = sorted(local)
        for i, a in enumerate(keys):
            for b in keys[i + 1:]:
                if b[0] == a[0]:
                    continue
                lo, hi = max(spans[a[0]][0], spans[b[0]][0]), min(spans[a[0]][1], spans[b[0]][1])
                if hi <= lo:
                    continue
                ta, tb = local[a][lo:hi], local[b][lo:hi]
                if ta.sum() < min_link_frames or tb.sum() < min_link_frames:
                    continue
                iou = (ta & tb).sum() / max(1, (ta | tb).sum())
                if iou >= cfg.link_agreement:
                    links.append((iou, a, b))
        for _, a, b in sorted(links, reverse=True):
            ra, rb = find(a), find(b)
            if ra == rb or members[ra] & members[rb]:
                continue  # already one speaker, or merging would join two speakers of the same window
            parent[rb] = ra
            members[ra] |= members.pop(rb)

        activity: dict[tuple[int, int], np.ndarray] = {}
        for key, track in local.items():
            root = find(key)
            activity[root] = activity[root] | track if root in activity else track.copy()

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
        per_speaker = {k: v for k, v in per_speaker.items() if v and sum(e - s for s, e in v) >= cfg.min_speaker_seconds}
        # Anonymous labels in order of first appearance (never "candidate" by position).
        order = sorted(per_speaker, key=lambda k: per_speaker[k][0][0])
        names = {key: f"S{i + 1}" for i, key in enumerate(order)}

        turns = sorted((s, e, names[k]) for k, segs in per_speaker.items() for s, e in segs)
        # Overlap: most of the windows covering a moment see >= 2 of their own speakers active at once.
        overlap_mask = (overlap_votes > 0) & (2 * overlap_votes >= np.maximum(coverage, 1))
        overlaps = [(s, e) for s, e in segments(overlap_mask) if e - s >= cfg.min_overlap_seconds]
        # Exclusive speech (used for each speaker's level) leaves out ANY moment two stitched speakers touch, too.
        count = np.zeros(n_grid, dtype=np.int8)
        for segs in per_speaker.values():
            for s, e in segs:
                count[int(s / grid):int(np.ceil(e / grid))] += 1
        overlap_mask |= count >= 2

        speakers = []
        for key in order:
            segs = per_speaker[key]
            mask = np.zeros(n_grid, dtype=bool)
            for s, e in segs:
                mask[int(s / grid):int(np.ceil(e / grid))] = True
            exclusive_mask = mask & ~overlap_mask
            exclusive = segments(exclusive_mask)
            audio = slice_spans(samples, exclusive)
            rms = float(np.sqrt(np.mean(np.square(audio, dtype=np.float64)))) if len(audio) else 0.0
            speakers.append(SpeakerInfo(
                label=names[key], speech_seconds=float(mask.sum() * grid),
                exclusive_seconds=float(sum(e - s for s, e in exclusive)),
                rms_dbfs=None if len(audio) == 0 else (-120.0 if rms <= 1e-6 else 20 * float(np.log10(rms))),
                exclusive=exclusive))
        return DiarizationResult(turns=turns, overlaps=overlaps, speakers=speakers)
