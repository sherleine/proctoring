"""Validates every example in examples/ against its JSON Schema (and nested payloads).

    python docs/contracts/validate_examples.py

Requires: pip install "jsonschema[format]>=4.23" referencing
Also used by the backend and AI-service test suites as the contract reference.
"""

import json
import sys
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource

HERE = Path(__file__).parent
SCHEMAS = {p.name: json.loads(p.read_text()) for p in (HERE / "schemas").glob("*.json")}
# Schemas reference each other by file name (e.g. "analysis-result.v1.json").
REGISTRY = Registry().with_resources(
    [(name, Resource.from_contents(s)) for name, s in SCHEMAS.items()]
    + [(s["$id"], Resource.from_contents(s)) for s in SCHEMAS.values()]
)


def validate(schema_name: str, instance: object) -> list[str]:
    validator = Draft202012Validator(SCHEMAS[schema_name], registry=REGISTRY, format_checker=FormatChecker())
    return [f"{'/'.join(map(str, e.absolute_path)) or '<root>'}: {e.message}" for e in validator.iter_errors(instance)]


def main() -> int:
    failures = 0
    for example in sorted((HERE / "examples").glob("*.json")):
        instance = json.loads(example.read_text())
        errors = validate(example.name, instance)
        # The stream envelope carries its payload as a JSON string: validate that too.
        if example.name == "stream-envelope.v1.json" and not errors:
            errors = validate("audio-event-message.v1.json", json.loads(instance["payload"]))
        failures += bool(errors)
        print(f"{'FAIL' if errors else 'PASS'}  {example.name}")
        for e in errors:
            print(f"      {e}")
    # Backward compatibility: Phase 1 session examples must still satisfy the frozen v1.0 schemas.
    history = {p.name: json.loads(p.read_text()) for p in (HERE / "schemas" / "history").glob("*.json")}
    history_registry = Registry().with_resources([(s["$id"], Resource.from_contents(s)) for s in history.values()])
    for example, frozen in [("session-create.request.v1.json", "session-create.request.v1.0.json"), ("session.v1.json", "session.v1.0.json")]:
        v = Draft202012Validator(history[frozen], registry=history_registry, format_checker=FormatChecker())
        errors = [e.message for e in v.iter_errors(json.loads((HERE / "examples" / example).read_text()))]
        failures += bool(errors)
        print(f"{'FAIL' if errors else 'PASS'}  {example} is still valid under frozen {frozen}")
        for e in errors:
            print(f"      {e}")

    # Negative checks: the schemas must reject obviously wrong input.
    bad_meta = json.loads((HERE / "examples" / "audio-event-metadata.v1.json").read_text())
    bad_meta["audio"]["sampleRate"] = 48000
    rejected = bool(validate("audio-event-metadata.v1.json", bad_meta))
    print(f"{'PASS' if rejected else 'FAIL'}  rejects a 48 kHz upload")
    failures += not rejected
    bad_result = json.loads((HERE / "examples" / "analysis-result.v1.json").read_text())
    bad_result["findings"][0]["type"] = "CHEATING_CONFIRMED"
    rejected = bool(validate("analysis-result.v1.json", bad_result))
    print(f"{'PASS' if rejected else 'FAIL'}  rejects a finding type outside the contract")
    failures += not rejected
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
