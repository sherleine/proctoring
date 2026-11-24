"""Validation against the versioned contract schemas (copies of docs/contracts/schemas in ./contracts).

The JSON Schemas are the source of truth shared with the backend; Pydantic models in this package only give
typed access to data that has already passed these checks.
"""
import json
from functools import lru_cache
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource

CONTRACTS_DIR = Path(__file__).with_name("contracts")

ENVELOPE = "stream-envelope.v1.json"
EVENT_MESSAGE = "audio-event-message.v1.json"
ANALYSIS_STARTED = "analysis-started.v1.json"
ANALYSIS_RESULT = "analysis-result.v1.json"


@lru_cache
def _registry() -> tuple[Registry, dict[str, dict]]:
    schemas = {p.name: json.loads(p.read_text(encoding="utf-8")) for p in CONTRACTS_DIR.glob("*.json")}
    registry = Registry().with_resources((s["$id"], Resource.from_contents(s)) for s in schemas.values())
    return registry, schemas


@lru_cache
def _validator(name: str) -> Draft202012Validator:
    registry, schemas = _registry()
    return Draft202012Validator(schemas[name], registry=registry, format_checker=FormatChecker())


def validation_errors(name: str, document: object) -> list[str]:
    """Human-readable violations ("path: message"); empty when the document is valid."""
    errors = sorted(_validator(name).iter_errors(document), key=lambda e: list(e.absolute_path))
    return [f"{'/'.join(map(str, e.absolute_path)) or '$'}: {e.message}" for e in errors]


def ensure_valid(name: str, document: object) -> None:
    errors = validation_errors(name, document)
    if errors:
        raise ValueError(f"{name}: " + "; ".join(errors[:5]))
