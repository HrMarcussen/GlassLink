"""GlassLink DMC - capture cockpit display windows on the sim PC and stream them to the DUs."""

from __future__ import annotations

import functools
import pathlib
import subprocess

_ROOT = pathlib.Path(__file__).resolve().parent.parent


def _read_version() -> str:
    try:
        return (_ROOT / "VERSION").read_text(encoding="utf-8").strip() or "0.0.0"
    except OSError:
        return "0.0.0"


__version__ = _read_version()      # single source of truth: the VERSION file at the repository root


def _read_firmware_version() -> str:
    try:
        return (_ROOT / "FIRMWARE_VERSION").read_text(encoding="utf-8").strip() or __version__
    except OSError:
        return __version__


# The release in which the DU firmware last changed. A DU older than this should be reflashed; a DU at or above
# it is current even when the DMC has moved on with releases that did not touch firmware/.
firmware_version = _read_firmware_version()


def version_tuple(v: str) -> tuple[int, ...]:
    out = []
    for part in str(v).split("."):
        digits = "".join(ch for ch in part if ch.isdigit())
        out.append(int(digits) if digits else 0)
    return tuple(out)


@functools.lru_cache(maxsize=1)
def build_id() -> str:
    """Short git commit the DMC runs from ("" outside a git checkout; "-dirty" with uncommitted changes)."""
    try:
        out = subprocess.run(["git", "describe", "--always", "--dirty", "--abbrev=7", "--exclude", "*"],
                             cwd=_ROOT, capture_output=True, text=True, timeout=5)
        return out.stdout.strip() if out.returncode == 0 else ""
    except Exception:  # noqa: BLE001
        return ""
