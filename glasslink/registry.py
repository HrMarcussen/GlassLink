"""Displays as data: add, change and remove displays while the DMC runs.

A display is an entry in the config's `displays` section (window match rule, client size, off-screen parking
position, optional capture overrides). The registry owns the DisplayWorkers, keeps the hub and the config file in
step, and tells the module manager when a display that a DU was showing goes away.
"""

from __future__ import annotations

import logging
import re
import threading
from typing import Any, Callable

from .config import save_config

log = logging.getLogger(__name__)

SIM_PROCESS = "FlightSimulator2024.exe"
SIM_CLASS = "AceApp"
TITLE_PREFIX = "GlassLink:"
NAME_RE = re.compile(r"^[a-z][a-z0-9_]{0,23}$")

# Off-screen parking grid: to the right of any realistic main monitor, 800 px apart so 768 px windows never overlap.
PARK_X0, PARK_Y0, PARK_DX, PARK_DY, PARK_COLS = 2600, 0, 800, 820, 4
EDITABLE = ("client_size", "position", "fps", "quality", "max_size")


class DisplayError(ValueError):
    """A request that cannot be carried out; the message is shown to the user."""


def next_parking_slot(displays: dict[str, dict[str, Any]]) -> list[int]:
    used = {tuple(d.get("position") or ()) for d in displays.values()}
    for i in range(64):
        pos = (PARK_X0 + (i % PARK_COLS) * PARK_DX, PARK_Y0 + (i // PARK_COLS) * PARK_DY)
        if pos not in used:
            return list(pos)
    raise DisplayError("no free parking slot")


def _pair(value: Any, what: str, lo: int, hi: int) -> list[int]:
    try:
        a, b = (int(v) for v in value)
    except Exception:  # noqa: BLE001
        raise DisplayError(f"{what} must be two whole numbers") from None
    if not (lo <= a <= hi and lo <= b <= hi):
        raise DisplayError(f"{what} {a},{b} is outside {lo}..{hi}")
    return [a, b]


def clean_fields(fields: dict[str, Any]) -> dict[str, Any]:
    """Validate the editable properties; returns only what may be written to the config."""
    out: dict[str, Any] = {}
    for key, value in fields.items():
        if key not in EDITABLE:
            continue
        if value in (None, ""):
            out[key] = None
        elif key == "client_size":
            out[key] = _pair(value, "size", 64, 4096)
        elif key == "position":
            out[key] = _pair(value, "position", -20000, 20000)
        elif key == "fps":
            v = float(value)
            if not 0.2 <= v <= 60:
                raise DisplayError("fps must be between 0.2 and 60")
            out[key] = v
        elif key == "quality":
            v = int(value)
            if not 30 <= v <= 100:
                raise DisplayError("quality must be between 30 and 100")
            out[key] = v
        elif key == "max_size":
            v = int(value)
            if not 64 <= v <= 4096:
                raise DisplayError("max_size must be between 64 and 4096")
            out[key] = v
    return out


class DisplayRegistry:
    def __init__(self, cfg: dict[str, Any], hub: Any, worker_factory: Callable[[str, dict[str, Any]], Any],
                 config_path: Any = None, save: Callable[[dict[str, Any], Any], None] = save_config) -> None:
        self.cfg = cfg
        self.hub = hub
        self.config_path = config_path
        self._make_worker = worker_factory
        self._save = save
        self._lock = threading.RLock()
        self.workers: dict[str, Any] = {}
        self.on_removed: Callable[[str], None] | None = None     # set by the server: unassign DUs showing it
        cfg.setdefault("displays", {})

    # -- lifecycle -----------------------------------------------------------------------------
    def start_all(self) -> None:
        for name, dcfg in self.cfg["displays"].items():
            self._start(name, dcfg)

    def stop_all(self) -> None:
        with self._lock:
            for w in list(self.workers.values()):
                w.stop()

    def _start(self, name: str, dcfg: dict[str, Any]) -> None:
        if self.hub.get(name) is None:
            self.hub.add(name)
        w = self._make_worker(name, dcfg)
        self.workers[name] = w
        w.start()

    def _persist(self) -> None:
        try:
            self._save(self.cfg, self.config_path)
        except Exception as exc:  # noqa: BLE001
            log.warning("could not save the config: %s", exc)

    # -- editing -------------------------------------------------------------------------------
    def add(self, name: str, fields: dict[str, Any] | None = None) -> dict[str, Any]:
        name = (name or "").strip().lower()
        if not NAME_RE.match(name):
            raise DisplayError("name: start with a letter, then letters, digits or _ (max 24), e.g. fo_pfd")
        with self._lock:
            if name in self.cfg["displays"]:
                raise DisplayError(f"display '{name}' already exists")
            dcfg: dict[str, Any] = {
                "match": {"process": SIM_PROCESS, "class": SIM_CLASS, "title": f"{TITLE_PREFIX}{name}"},
                "client_size": [768, 768],
                "position": next_parking_slot(self.cfg["displays"]),
            }
            dcfg.update({k: v for k, v in clean_fields(fields or {}).items() if v is not None})
            self.cfg["displays"][name] = dcfg
            self._persist()
            self._start(name, dcfg)
            log.info("display '%s' added: %s", name, dcfg)
            return dict(dcfg)

    def update(self, name: str, fields: dict[str, Any]) -> dict[str, Any]:
        with self._lock:
            dcfg = self.cfg["displays"].get(name)
            if dcfg is None:
                raise DisplayError(f"unknown display '{name}'")
            changes = clean_fields(fields)
            if not changes:
                return dict(dcfg)
            for k, v in changes.items():
                if v is None:
                    dcfg.pop(k, None)
                else:
                    dcfg[k] = v
            self._persist()
            old = self.workers.pop(name, None)           # a fresh worker re-applies size, position and rates
            if old is not None:
                old.stop()
            self._start(name, dcfg)
            log.info("display '%s' updated: %s", name, changes)
            return dict(dcfg)

    def remove(self, name: str) -> None:
        with self._lock:
            if name not in self.cfg["displays"]:
                raise DisplayError(f"unknown display '{name}'")
            w = self.workers.pop(name, None)
            if w is not None:
                w.stop()
            del self.cfg["displays"][name]
            self.hub.remove(name)
            self._persist()
        if self.on_removed:
            try:
                self.on_removed(name)
            except Exception:  # noqa: BLE001
                log.exception("on_removed hook failed for '%s'", name)
        log.info("display '%s' removed", name)

    def describe(self, points: dict[str, Any] | None = None) -> dict[str, dict[str, Any]]:
        """Config view for the editor: the editable fields plus whether a pop-out click point is known."""
        with self._lock:
            out = {}
            for name, d in self.cfg["displays"].items():
                out[name] = {k: d.get(k) for k in EDITABLE}
                out[name]["title"] = (d.get("match") or {}).get("title")
                out[name]["sim_window"] = (d.get("match") or {}).get("process") == SIM_PROCESS
                p = (points or {}).get(name)
                out[name]["has_point"] = p is not None
                out[name]["point_view"] = None if p is None else (
                    "FO seat" if isinstance(p, dict) and (p.get("camera") or {}).get("mode") == "view" else "captain seat")
                if isinstance(p, dict) and (p.get("camera") or {}).get("mode") == "custom":
                    out[name].update(has_point=False, point_view=None)      # sim custom cameras: no longer used
            return out
