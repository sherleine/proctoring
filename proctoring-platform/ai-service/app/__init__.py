"""Proctoring AI service. Builds the main router (mounted by main.py) from every feature router."""
import os

# NumPy's OpenBLAS reserves a buffer per CPU thread when it is first imported; on many-core laptops this can
# fail with "OpenBLAS error: Memory allocation still failed" when memory is tight. Models run their own
# threading, so a small BLAS pool is enough. An explicit environment value still wins.
os.environ.setdefault("OPENBLAS_NUM_THREADS", "4")

# pyannote.audio 4.x sends usage telemetry (model/pipeline init, per-file duration and speaker settings) to
# https://otel.pyannote.ai unless PYANNOTE_METRICS_ENABLED is "false". It is read when pyannote is imported, so it
# is forced off here, before any pyannote import. Exam metadata never leaves this machine.
os.environ["PYANNOTE_METRICS_ENABLED"] = "false"

from fastapi import APIRouter  # noqa: E402  (after the environment settings above)

from app.api.health import router as health_router  # noqa: E402
from app.api.voice_verify import router as voice_verify_router  # noqa: E402

router = APIRouter()
router.include_router(health_router)
router.include_router(voice_verify_router)
