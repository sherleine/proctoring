"""Downloads the model files that are not Python packages and verifies their SHA-256.

    python scripts/fetch_models.py

Silero VAD ships inside its pip package; Whisper (optional) downloads itself on first use.
Files already present with the right checksum are skipped.
"""
import hashlib
import sys
import urllib.request
from pathlib import Path

SERVICE_ROOT = Path(__file__).resolve().parents[1]

MODELS = [
    {
        "name": "CAM++ speaker embedding (3D-Speaker, VoxCeleb, English) - weights Apache-2.0",
        "path": "models/speaker/3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx",
        "url": "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/"
               "3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx",
        "sha256": "98daf9bcf1c8abf3db374fec4c27a5eda7c58705bb3f180d057b085a72a88bb5",
    },
]


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> int:
    failed = 0
    for model in MODELS:
        target = SERVICE_ROOT / model["path"]
        if target.is_file() and sha256(target) == model["sha256"]:
            print(f"ok       {model['path']} (already present)")
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        partial = target.with_suffix(target.suffix + ".part")
        print(f"download {model['name']}")
        urllib.request.urlretrieve(model["url"], partial)
        if sha256(partial) != model["sha256"]:
            partial.unlink(missing_ok=True)
            print(f"FAILED   {model['path']}: checksum mismatch (file removed)")
            failed += 1
            continue
        partial.replace(target)
        print(f"ok       {model['path']}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
