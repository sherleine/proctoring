"""Run with the AI service venv from the repository root:
    ai_service\\.venv\\Scripts\\python -m pytest tests/ai_service
"""

import sys
from pathlib import Path

AI_DIR = Path(__file__).resolve().parents[2] / "ai_service"
sys.path.insert(0, str(AI_DIR))
