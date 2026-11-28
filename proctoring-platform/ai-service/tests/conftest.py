"""Pytest setup shared by all test folders."""
import app  # noqa: F401  (applies the OpenBLAS thread limit before numpy-heavy imports)
