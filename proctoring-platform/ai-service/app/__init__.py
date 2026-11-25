"""Proctoring AI service: Redis worker + health API (see ai-service/README.md)."""
import os

# NumPy's OpenBLAS reserves a buffer per CPU thread when it is first imported; on many-core laptops this can
# fail with "OpenBLAS error: Memory allocation still failed" when memory is tight. Models run their own
# threading, so a small BLAS pool is enough. An explicit environment value still wins.
os.environ.setdefault("OPENBLAS_NUM_THREADS", "4")

# pyannote.audio 4.x sends usage telemetry (model/pipeline init, per-file duration and speaker settings) to
# https://otel.pyannote.ai unless PYANNOTE_METRICS_ENABLED is "false". It is read when pyannote is imported, so it
# is forced off here, before any pyannote import. Exam metadata never leaves this machine.
os.environ["PYANNOTE_METRICS_ENABLED"] = "false"
