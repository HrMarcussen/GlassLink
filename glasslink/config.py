"""Configuration loading / saving (plain JSON, see config.example.json)."""

from __future__ import annotations

import copy
import json
import os
import shutil
import threading
from pathlib import Path
from typing import Any

DEFAULTS: dict[str, Any] = {
    "server": {"host": "0.0.0.0", "port": 8765},
    # fps: capture rate of a display that is in use (a DU is assigned or a viewer is connected). Real DUs and the
    # Fenix redraw at 20-25 Hz. idle_fps: rate of a display nobody is looking at (status page thumbnails only).
    # fps is the capture cap. 40, not the ~20 the aircraft draws at: the cap works as a minimum interval between
    # frames, and an interval longer than the sim's frame time lets only every second frame through (measured
    # 19 Sept 2026: cap 24 = 41 ms against a sim at 26 fps = 38 ms gave 13 fps). With 25 ms the result is never
    # below 20 for any sim rate of 20 or more. Unchanged frames are dropped before encoding, so this costs little.
    "capture": {"backend": "auto", "fps": 40, "idle_fps": 1, "idle_after_s": 5, "quality": 85, "subsampling": "420"},
    # Keep the DMC out of the simulator's way: below-normal priority, and only the last logical CPUs ("auto" =
    # the last third on machines with 8 or more; a list of CPU numbers, or null for all).
    "process": {"priority": "below_normal", "affinity": "auto"},
    "popout": {"auto": True, "aircraft": "Fenix", "zoom": 30, "grace_s": 10, "retry_s": 60,
               "camera_restore": "current"},
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


_save_lock = threading.Lock()


def load_config(path: Path | None = None) -> dict[str, Any]:
    """The user's file with the defaults filled in underneath. Every section is merged (not only some), and keys this
    version does not know are kept, so saving never drops what the .NET DMC or the user put there (#49)."""
    path = path or default_config_path()
    user: dict[str, Any] = {}
    if path.exists():
        try:
            with path.open("r", encoding="utf-8") as fh:
                user = json.load(fh)
        except (json.JSONDecodeError, UnicodeDecodeError):
            backup = path.with_name(path.name + ".bak")          # a save cut short: the previous one is next to it
            with backup.open("r", encoding="utf-8") as fh:
                user = json.load(fh)
    cfg = copy.deepcopy(DEFAULTS)
    for key, value in user.items():
        if isinstance(value, dict) and isinstance(cfg.get(key), dict) and key not in ("modules", "displays"):
            cfg[key].update(value)
        else:
            cfg[key] = value
    for name, d in cfg["displays"].items():
        merged = copy.deepcopy(DISPLAY_DEFAULTS)
        merged.update(d)
        cfg["displays"][name] = merged
    return cfg


def override(cfg: dict[str, Any], section: str, key: str, value: Any) -> None:
    """A command-line setting for this run only: used like any other, never written to the file (#49)."""
    saved = cfg.setdefault("_overrides", {})
    saved.setdefault((section, key), (key in cfg[section], cfg[section].get(key)))
    cfg[section][key] = value


def save_config(cfg: dict[str, Any], path: Path | None = None) -> Path:
    """Writes to a temporary file, flushes it to disk and swaps it in, keeping the previous file as .bak; one save at
    a time, from whichever thread (#49)."""
    path = path or default_config_path()
    with _save_lock:
        for attempt in range(3):                                   # another thread may be changing the dicts right now
            try:
                out = copy.deepcopy({k: v for k, v in cfg.items() if not k.startswith("_")})
                break
            except RuntimeError:
                if attempt == 2:
                    raise
        for (section, key), (had, value) in (cfg.get("_overrides") or {}).items():
            if had:
                out[section][key] = value
            else:
                out[section].pop(key, None)
        # Drop None-valued defaults for a tidy file.
        for d in out.get("displays", {}).values():
            for k in list(d.keys()):
                if d[k] is None:
                    del d[k]
        temp = path.with_name(path.name + ".tmp")
        with temp.open("w", encoding="utf-8") as fh:
            json.dump(out, fh, indent=2)
            fh.write("\n")
            fh.flush()
            os.fsync(fh.fileno())
        if path.exists():
            shutil.copy2(path, path.with_name(path.name + ".bak"))
        os.replace(temp, path)
    return path


def display_setting(cfg: dict[str, Any], name: str, key: str) -> Any:
    """Per-display override falling back to the global capture section."""
    d = cfg["displays"][name]
    if d.get(key) is not None:
        return d[key]
    return cfg["capture"].get(key)
