"""DU firmware images: locate the built image and read its ESP-IDF application descriptor."""

from __future__ import annotations

import pathlib
import struct
import time
import zlib
from typing import Any

_ROOT = pathlib.Path(__file__).resolve().parent.parent
DEFAULT_IMAGE = _ROOT / "firmware" / "build" / "glasslink_du.bin"
PROJECT_NAME = "glasslink_du"
_APP_DESC_OFFSET = 32               # image header (24) + first segment header (8)
_APP_DESC_MAGIC = 0xABCD5432
MAX_IMAGE = 0x400000                # the DU's OTA slots are 4 MiB

OTA_ERRORS = {
    1: "the DU could not start the update (no free slot or image too large)",
    2: "flash write failed on the DU",
    3: "the DU rejected the image (not a valid application)",
    4: "checksum mismatch",
    5: "size mismatch",
    6: "the DU timed out waiting for data",
    7: "data arrived out of order",
}


def image_path(cfg: dict[str, Any]) -> pathlib.Path:
    p = (cfg.get("firmware") or {}).get("image")
    return pathlib.Path(p) if p else DEFAULT_IMAGE


def describe(data: bytes) -> dict[str, Any]:
    """Version and project name from the application descriptor; raises ValueError if this is not a DU image."""
    if len(data) < _APP_DESC_OFFSET + 256 or data[0] != 0xE9:
        raise ValueError("not an ESP application image")
    if len(data) > MAX_IMAGE:
        raise ValueError(f"image is {len(data)} bytes, the DU's update slot holds {MAX_IMAGE}")
    magic, = struct.unpack_from("<I", data, _APP_DESC_OFFSET)
    if magic != _APP_DESC_MAGIC:
        raise ValueError("application descriptor not found")

    def text(off: int, n: int) -> str:
        return data[_APP_DESC_OFFSET + off:_APP_DESC_OFFSET + off + n].split(b"\0", 1)[0].decode("ascii", "replace")

    info = {"version": text(16, 32), "project": text(48, 32), "built": f"{text(96, 16)} {text(80, 16)}",
            "idf": text(112, 32), "size": len(data), "crc32": zlib.crc32(data) & 0xFFFFFFFF}
    if info["project"] != PROJECT_NAME:
        raise ValueError(f"image is for project '{info['project']}', expected '{PROJECT_NAME}'")
    return info


def load(cfg: dict[str, Any]) -> tuple[bytes, dict[str, Any]]:
    p = image_path(cfg)
    if not p.exists():
        raise ValueError(f"no firmware image at {p} (build the firmware first)")
    data = p.read_bytes()
    info = describe(data)
    info["path"] = str(p)
    info["modified"] = time.strftime("%Y-%m-%d %H:%M", time.localtime(p.stat().st_mtime))
    return data, info


def summary(cfg: dict[str, Any]) -> dict[str, Any]:
    """For the status page: what an update would install, or why none is available."""
    try:
        _, info = load(cfg)
        return {"available": True, **{k: info[k] for k in ("version", "size", "modified", "path")}}
    except (ValueError, OSError) as exc:
        return {"available": False, "error": str(exc)}
