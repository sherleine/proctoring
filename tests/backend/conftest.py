"""Run with the backend venv from the repository root:
    backend\\.venv\\Scripts\\python -m pytest tests/backend
"""

import sys
from pathlib import Path

BACKEND_DIR = Path(__file__).resolve().parents[2] / "backend"
sys.path.insert(0, str(BACKEND_DIR))
