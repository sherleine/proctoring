"""Independent benchmark of pyannote.audio + speaker-diarization-community-1 (NOT used by the service yet).

Run with the isolated environment:
    .venv-diarization\\Scripts\\python -m evaluation.bench_diarization <LibriSpeech/test-clean> [--offline]

Measures (process-level, Windows): import time/memory, model download size, pipeline load time/memory, and for
10 s clips (1 speaker, 2 speakers taking turns, 2 speakers overlapping, silence): median processing time,
real-time factor, CPU utilisation (process CPU time / wall time, in cores) and peak working set.
Audio is passed in memory ({"waveform", "sample_rate"}), so no file decoding is involved.
The token is read from ai-service/.env (HF_TOKEN) and never printed. Results -> evaluation/results/bench_diarization.json
"""
from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import json
import os
import statistics
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MODEL = "pyannote/speaker-diarization-community-1"

# pyannote.audio 4.x sends usage telemetry (model/pipeline init, per-file duration and speaker settings) to
# https://otel.pyannote.ai unless this is "false". Must be set BEFORE pyannote is imported. Exam metadata stays local.
os.environ["PYANNOTE_METRICS_ENABLED"] = "false"
SR = 16_000


class _PMC(ctypes.Structure):
    _fields_ = [("cb", wt.DWORD), ("PageFaultCount", wt.DWORD), ("PeakWorkingSetSize", ctypes.c_size_t),
                ("WorkingSetSize", ctypes.c_size_t)] + [(f"x{i}", ctypes.c_size_t) for i in range(6)]


_k32 = ctypes.WinDLL("kernel32")
_k32.GetCurrentProcess.restype = wt.HANDLE
_ps = ctypes.WinDLL("psapi")
_ps.GetProcessMemoryInfo.argtypes = [wt.HANDLE, ctypes.POINTER(_PMC), wt.DWORD]


def mem_mb() -> tuple[float, float]:
    c = _PMC()
    c.cb = ctypes.sizeof(c)
    _ps.GetProcessMemoryInfo(_k32.GetCurrentProcess(), ctypes.byref(c), c.cb)
    return c.WorkingSetSize / 2**20, c.PeakWorkingSetSize / 2**20


def cpu_seconds() -> float:
    t = os.times()
    return t.user + t.system


def dir_mb(path: Path) -> float:
    return sum(f.stat().st_size for f in path.rglob("*") if f.is_file()) / 2**20 if path.exists() else 0.0


def read_env() -> dict[str, str]:
    env = {}
    p = ROOT / ".env"
    if p.exists():
        for line in p.read_text(encoding="utf-8").splitlines():
            if "=" in line and not line.lstrip().startswith("#"):
                k, v = line.split("=", 1)
                env[k.strip()] = v.strip().strip('"')
    return env


def clips(libri: Path):
    import numpy as np
    import soundfile as sf

    def utt(spk: str, skip: int, seconds: float) -> np.ndarray:
        files = sorted((libri / spk).rglob("*.flac"))[skip:]
        out, total = [], 0
        for f in files:
            a, _ = sf.read(f, dtype="float32")
            out.append(a)
            total += len(a)
            if total >= seconds * SR:
                break
        return np.concatenate(out)[: int(seconds * SR)]

    spk = sorted(p.name for p in libri.iterdir() if p.is_dir())
    a, b = spk[1], spk[3]
    single = utt(a, 0, 10)
    turns = np.concatenate([utt(a, 3, 4.5), np.zeros(int(0.5 * SR), np.float32), utt(b, 0, 5)])
    overlap = utt(a, 6, 10).copy()
    overlap[int(3 * SR):int(7 * SR)] += utt(b, 3, 4) * 0.9  # 4 s of simultaneous speech
    silence = np.zeros(10 * SR, np.float32)
    return {"1-speaker": single, "2-speakers-turns": turns, "2-speakers-overlap-4s": np.clip(overlap, -1, 1),
            "silence": silence}, (a, b)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("librispeech", type=Path)
    ap.add_argument("--offline", action="store_true", help="HF_HUB_OFFLINE=1: proves the cached model loads without network")
    ap.add_argument("--runs", type=int, default=5)
    ap.add_argument("--threads", type=int, default=4)
    args = ap.parse_args()

    env = read_env()
    os.environ["HF_HOME"] = str((ROOT / env.get("HF_HOME", "models/hf")).resolve())
    if args.offline:
        os.environ["HF_HUB_OFFLINE"] = "1"
    token = env.get("HF_TOKEN") or None
    report: dict = {"model": MODEL, "offline": args.offline, "threads": args.threads}

    m0 = mem_mb()[0]
    t = time.perf_counter()
    import torch
    torch.set_num_threads(args.threads)
    import pyannote.audio
    from pyannote.audio import Pipeline
    report["versions"] = {"python": sys.version.split()[0], "torch": torch.__version__,
                          "pyannote.audio": pyannote.audio.__version__}
    report["import"] = {"seconds": round(time.perf_counter() - t, 2), "addedMB": round(mem_mb()[0] - m0, 1)}

    from huggingface_hub import HfApi
    hub = Path(os.environ["HF_HOME"]) / "hub"
    if not args.offline:
        info = HfApi().model_info(MODEL, token=token)
        report["revision"] = info.sha
    else:  # the pinned revision = the snapshot already in the local cache (no network)
        snaps = list(hub.glob("models--pyannote--speaker-diarization-community-1/snapshots/*"))
        report["revision"] = snaps[0].name if snaps else None
    before_dl = dir_mb(hub)
    m1 = mem_mb()[0]
    t = time.perf_counter()
    pipeline = Pipeline.from_pretrained(MODEL, token=token, revision=report.get("revision"))
    pipeline.to(torch.device("cpu"))
    report["load"] = {"seconds": round(time.perf_counter() - t, 2), "addedMB": round(mem_mb()[0] - m1, 1)}
    report["modelCacheMB"] = round(dir_mb(hub), 1)
    report["downloadedThisRunMB"] = round(dir_mb(hub) - before_dl, 1)

    data, speakers = clips(args.librispeech)
    report["speakers"] = speakers
    report["clips"] = {}
    for name, audio in data.items():
        waveform = {"waveform": torch.from_numpy(audio).unsqueeze(0), "sample_rate": SR}
        pipeline(waveform)  # warm-up
        times, cores = [], []
        out = None
        for _ in range(args.runs):
            c0, w0 = cpu_seconds(), time.perf_counter()
            out = pipeline(waveform)
            wall = time.perf_counter() - w0
            times.append(wall)
            cores.append((cpu_seconds() - c0) / wall)
        diar = out.speaker_diarization
        turns = [(round(seg.start, 2), round(seg.end, 2), spk) for seg, _, spk in diar.itertracks(yield_label=True)]
        overlap = diar.get_overlap()
        med = statistics.median(times)
        report["clips"][name] = {
            "seconds": round(len(audio) / SR, 2), "medianSeconds": round(med, 3), "min": round(min(times), 3),
            "max": round(max(times), 3), "rtf": round(med / (len(audio) / SR), 4),
            "cpuCores": round(statistics.median(cores), 2),
            "speakers": len(diar.labels()), "turns": turns,
            "overlapSeconds": round(sum(s.duration for s in overlap), 2),
        }
        print(f"{name:22} median {med * 1000:7.0f} ms  RTF {med / (len(audio) / SR):.3f}  "
              f"CPU {statistics.median(cores):.2f} cores  speakers {len(diar.labels())}  "
              f"overlap {report['clips'][name]['overlapSeconds']} s  turns {turns}", flush=True)
    now, peak = mem_mb()
    report["memoryMB"] = {"workingSetAfter": round(now, 1), "peakWorkingSet": round(peak, 1)}
    report["gpu"] = "not used: CPU-only torch build (torch.cuda.is_available() = %s)" % torch.cuda.is_available()
    out_path = ROOT / "evaluation" / "results" / ("bench_diarization_offline.json" if args.offline else "bench_diarization.json")
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "clips"}, indent=1))


if __name__ == "__main__":
    main()
