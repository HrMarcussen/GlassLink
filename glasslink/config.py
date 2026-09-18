"""Configuration loading / saving (plain JSON, see config.example.json)."""

from __future__ import annotations

import copy
import json
import os
from pathlib import Path
from typing import Any

DEFAULTS: dict[str, Any] = {
    "server": {"host": "0.0.0.0", "port": 8765},
    # fps: capture rate of a display that is in use (a DU is assigned or a viewer is connected). Real DUs and the
    # Fenix redraw at 20-25 Hz. idle_fps: rate of a display nobody is looking at (status page thumbnails only).
    "capture": {"backend": "auto", "fps": 24, "idle_fps": 1, "idle_after_s": 5, "quality": 85, "subsampling": "420"},
    # Keep the DMC out of the simulator's way: below-normal priority, and only the last logical CPUs ("auto" =
    # the last third on machines with 8 or more; a list of CPU numbers, or null for all).
    "process": {"priority": "below_normal", "affinity": "auto"},
    "popout": {"auto": True, "aircraft": "Fenix", "zoom": 30, "grace_s": 10, "retry_s": 60,
               "camera_restore": "current", "camera_slot": 9},
    "usb": {"enabled": True, "scan_interval_s": 2},
    # DU brightness follows the cockpit knobs: L:vars read through SimConnect (source "simconnect", nothing else
    # needed) or FSUIPC7's WASM DLL (source "fsuipc_wasm"). Per-aircraft variable names live in the pop-out
    # profile, key "brightness". The module slider then acts as a trim (max).
    "brightness": {"enabled": True, "source": "simconnect", "interval_s": 0.1,
                   "wapi_dll": r"C:\FSUIPC7\Utils\FSUIPC_WAPID.dll", "wasm_hz": 12},
    "modules": {},   # "<serial>": {"display": "pfd", "brightness": 100, "rotation": 0, "label": "Capt PFD"}
    "displays": {},
}

DISPLAY_DEFAULTS: dict[str, Any] = {
    "match": {},
    "client_size": None,   # [w, h] applied to the window when found (None = leave as is)
    "position": None,      # [x, y] applied to the window when found (None = leave as is)
    "crop": None,          # [x, y, w, h] override in captured-frame pixels (None = auto client-area crop)
    "max_size": None,      # downscale so that max(w, h) <= max_size before encoding (None = off)
    "fps": None,           # per-display override of capture.fps
    "quality": None,       # per-display override of capture.quality
}


def default_config_path() -> Path:
    return Path(os.environ.get("GLASSLINK_CONFIG", "config.json"))


def load_config(path: Path | None = None) -> dict[str, Any]:
    path = path or default_config_path()
    cfg = copy.deepcopy(DEFAULTS)
    if path.exists():
        with path.open("r", encoding="utf-8") as fh:
            user = json.load(fh)
        for section in ("server", "capture", "popout", "usb"):
            cfg[section].update(user.get(section, {}))
        cfg["modules"] = user.get("modules", {})
        cfg["displays"] = user.get("displays", {})
    for name, d in cfg["displays"].items():
        merged = copy.deepcopy(DISPLAY_DEFAULTS)
        merged.update(d)
        cfg["displays"][name] = merged
    return cfg


def save_config(cfg: dict[str, Any], path: Path | None = None) -> Path:
    path = path or default_config_path()
    out = copy.deepcopy(cfg)
    out.pop("_path", None)
    # Drop None-valued defaults for a tidy file.
    for d in out.get("displays", {}).values():
        for k in list(d.keys()):
            if d[k] is None:
                del d[k]
    with path.open("w", encoding="utf-8") as fh:
        json.dump(out, fh, indent=2)
        fh.write("\n")
    return path


def display_setting(cfg: dict[str, Any], name: str, key: str) -> Any:
    """Per-display override falling back to the global capture section."""
    d = cfg["displays"][name]
    if d.get(key) is not None:
        return d[key]
    return cfg["capture"].get(key)
