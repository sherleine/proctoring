"""Checks whether optional Python packages are installed without importing them.

Importing torch/pyannote/tensorflow takes seconds; the health endpoint only
needs to know whether they are present and which version.
"""

from importlib import metadata, util


def package_status(import_name: str, dist_name: str | None = None) -> dict[str, str | bool | None]:
    installed = util.find_spec(import_name) is not None
    version = None
    if installed:
        try:
            version = metadata.version(dist_name or import_name)
        except metadata.PackageNotFoundError:
            version = None
    return {"installed": installed, "version": version}
