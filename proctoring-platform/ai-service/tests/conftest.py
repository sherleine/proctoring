"""Pytest fixtures shared by all test folders."""
from pathlib import Path

import pytest

import app  # noqa: F401  (OpenBLAS thread limit before numpy-heavy imports)


@pytest.fixture
def store_root(tmp_path: Path) -> Path:
    root = tmp_path / "audio"
    root.mkdir()
    return root
