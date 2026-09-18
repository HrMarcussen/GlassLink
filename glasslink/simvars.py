"""Sim variables (L:vars) for the brightness link. Two backends, same interface:

* `SimConnectVars` (default): MSFS 2024 SimConnect accepts "L:NAME" in data definitions, so the variables are
  read straight from the sim with nothing else installed or running (verified 17 Sept 2026 with the Fenix).
  Only the variables that are asked for are requested. The aircraft title is read too, so the per-aircraft
  profile can be chosen by name.
* `WapiVars`: FSUIPC7's WASM interface DLL (`Utils\\FSUIPC_WAPID.dll`). Kept as a fallback; it depends on the
  FSUIPC WASM module inside the sim. Signatures were taken from the P/Invokes in FSUIPC's WebSocket server.

`get(name)` is thread-safe and returns None while the sim, the aircraft or the variable is not there.
Names are without the "L:" prefix.
"""

from __future__ import annotations

import ctypes
import logging
import os
import threading
import time
from typing import Any

log = logging.getLogger(__name__)

DEFAULT_DLL = r"C:\FSUIPC7\Utils\FSUIPC_WAPID.dll"

_LOG_CB = ctypes.WINFUNCTYPE(None, ctypes.c_char_p)
_LIST_CB = ctypes.WINFUNCTYPE(None, ctypes.c_int, ctypes.c_char_p)
_VALUES_CB = ctypes.WINFUNCTYPE(None, ctypes.c_char_p, ctypes.c_double)


class WapiVars(threading.Thread):
    def __init__(self, dll_path: str | None = None, interval_s: float = 0.1, wasm_hz: int = 12) -> None:
        super().__init__(name="simvars", daemon=True)
        self.dll_path = dll_path or DEFAULT_DLL
        self.interval = max(0.05, float(interval_s))
        self.wasm_hz = int(wasm_hz or 0)      # update rate requested from the WASM module (0 = leave as is)
        self.values: dict[str, float] = {}
        self.names: set[str] = set()
        self.running = False
        self.error = ""
        self.last_update = 0.0
        self._stop_evt = threading.Event()
        self._lock = threading.Lock()
        self._dll: Any = None
        self._keep: list[Any] = []       # ctypes callbacks must outlive the DLL calls
        self._polls = 0

    # -- public ------------------------------------------------------------------------------
    def get(self, name: str) -> float | None:
        with self._lock:
            return self.values.get(name)

    def has(self, name: str) -> bool:
        return name in self.names

    def title(self) -> str | None:
        return None                      # the WAPI has no aircraft title; profiles are chosen by variable names

    def status(self) -> dict[str, Any]:
        return {
            "source": "fsuipc_wasm",
            "running": self.running,
            "error": self.error,
            "lvars": len(self.names),
            "age_s": round(time.time() - self.last_update, 1) if self.last_update else None,
            "dll": self.dll_path,
        }

    def stop(self) -> None:
        self._stop_evt.set()

    # -- thread ------------------------------------------------------------------------------
    def run(self) -> None:
        while not self._stop_evt.is_set():
            if not self._connect():
                self._stop_evt.wait(10.0)
                continue
            while not self._stop_evt.is_set() and self._dll.fsuipcw_isRunning():
                try:
                    self._poll()
                except Exception as exc:  # noqa: BLE001
                    log.warning("simvars poll failed: %s", exc)
                    break
                self._stop_evt.wait(self.interval)
            self._disconnect()
            if not self._stop_evt.is_set():
                self._stop_evt.wait(5.0)

    def _connect(self) -> bool:
        if not os.path.exists(self.dll_path):
            self.error = f"{self.dll_path} not found (FSUIPC7 installed?)"
            return False
        try:
            d = ctypes.WinDLL(self.dll_path)
            d.fsuipcw_init.argtypes = [_LOG_CB]
            d.fsuipcw_init.restype = None
            d.fsuipcw_start.restype = None
            d.fsuipcw_end.restype = None
            d.fsuipcw_isRunning.restype = ctypes.c_ubyte
            d.fsuipcw_setLogLevel.argtypes = [ctypes.c_int]
            d.fsuipcw_getLvarList.argtypes = [_LIST_CB]
            d.fsuipcw_getLvarValues.argtypes = [_VALUES_CB]
            d.fsuipcw_getLvarUpdateFrequency.restype = ctypes.c_int
            d.fsuipcw_setLvarUpdateFrequency.argtypes = [ctypes.c_int]
            log_cb = _LOG_CB(lambda s: log.debug("wapi: %s", s.decode("utf-8", "replace").strip()))
            self._keep = [log_cb]
            d.fsuipcw_init(log_cb)
            d.fsuipcw_setLogLevel(2)        # info
            d.fsuipcw_start()
            for _ in range(40):             # the DLL connects to the sim in the background
                if d.fsuipcw_isRunning():
                    break
                if self._stop_evt.wait(0.25):
                    return False
            if not d.fsuipcw_isRunning():
                d.fsuipcw_end()
                self.error = "sim not running (WASM interface did not connect)"
                return False
            self._dll = d
            self._refresh_names()
            freq = d.fsuipcw_getLvarUpdateFrequency()
            if self.wasm_hz and freq < self.wasm_hz:
                d.fsuipcw_setLvarUpdateFrequency(self.wasm_hz)
                log.info("simvars: WASM L:var update rate %d -> %d Hz", freq, self.wasm_hz)
            self.running = True
            self.error = ""
            log.info("simvars: FSUIPC WASM interface connected, %d L:vars, update rate %d Hz", len(self.names),
                     d.fsuipcw_getLvarUpdateFrequency())
            return True
        except Exception as exc:  # noqa: BLE001
            self.error = f"{type(exc).__name__}: {exc}"
            log.warning("simvars: %s", self.error)
            return False

    def _disconnect(self) -> None:
        self.running = False
        if self._dll is not None:
            try:
                self._dll.fsuipcw_end()
            except Exception:  # noqa: BLE001
                pass
            self._dll = None
        with self._lock:
            self.values = {}

    def _refresh_names(self) -> None:
        names: set[str] = set()
        cb = _LIST_CB(lambda i, n: names.add(n.decode("utf-8", "replace")))
        self._dll.fsuipcw_getLvarList(cb)
        self.names = names

    def _poll(self) -> None:
        vals: dict[str, float] = {}
        cb = _VALUES_CB(lambda n, v: vals.__setitem__(n.decode("utf-8", "replace"), v))
        self._dll.fsuipcw_getLvarValues(cb)
        with self._lock:
            self.values = vals
        self.last_update = time.time()
        self._polls += 1
        # The list arrives from the WASM module shortly after connecting and changes when an aircraft loads:
        # refresh every poll until it is non-empty, then every ~10 s.
        if not self.names or self._polls % 40 == 0:
            self._refresh_names()


class SimConnectVars(threading.Thread):
    """L:vars through SimConnect data definitions (MSFS 2024). Requests are created on demand by `get`."""

    def __init__(self, interval_s: float = 0.1) -> None:
        super().__init__(name="simvars", daemon=True)
        self.interval = max(0.05, float(interval_s))
        self.values: dict[str, float] = {}
        self.names: set[str] = set()        # names asked for so far
        self.running = False
        self.error = ""
        self.last_update = 0.0
        self._title = ""
        self._stop_evt = threading.Event()
        self._lock = threading.Lock()
        self._sm: Any = None
        self._reqs: dict[str, Any] = {}
        self._title_req: Any = None
        self._wanted: set[str] = set()

    # -- public ------------------------------------------------------------------------------
    def get(self, name: str) -> float | None:
        with self._lock:
            if name not in self._wanted:
                self._wanted.add(name)      # picked up by the poll thread
            return self.values.get(name)

    def has(self, name: str) -> bool:
        return True                          # SimConnect cannot list L:vars; an unknown one just reads 0

    def title(self) -> str | None:
        return self._title or None

    def status(self) -> dict[str, Any]:
        return {
            "source": "simconnect",
            "running": self.running,
            "error": self.error,
            "lvars": len(self.names),
            "age_s": round(time.time() - self.last_update, 1) if self.last_update else None,
            "aircraft": self._title,
        }

    def stop(self) -> None:
        self._stop_evt.set()

    # -- thread ------------------------------------------------------------------------------
    def run(self) -> None:
        while not self._stop_evt.is_set():
            if not self._connect():
                self._stop_evt.wait(10.0)
                continue
            while not self._stop_evt.is_set():
                try:
                    self._poll()
                except Exception as exc:  # noqa: BLE001
                    log.info("simvars: sim connection lost (%s)", exc)
                    break
                self._stop_evt.wait(self.interval)
            self._disconnect()
            if not self._stop_evt.is_set():
                self._stop_evt.wait(5.0)

    def _connect(self) -> bool:
        try:
            from SimConnect import Request, SimConnect  # lazy: only on the sim PC

            self._sm = SimConnect()
            self._title_req = Request((b"TITLE", b"String"), self._sm, _time=1000)
            self._reqs = {}
            self.running = True
            self.error = ""
            log.info("simvars: SimConnect connected")
            return True
        except Exception as exc:  # noqa: BLE001
            self.error = f"sim not running ({type(exc).__name__})"
            self._sm = None
            return False

    def _disconnect(self) -> None:
        self.running = False
        if self._sm is not None:
            try:
                self._sm.exit()
            except Exception:  # noqa: BLE001
                pass
            self._sm = None
        self._reqs = {}
        with self._lock:
            self.values = {}
        self._title = ""

    def _poll(self) -> None:
        from SimConnect import Request

        v = self._title_req.value
        self._title = v.decode("utf-8", "ignore") if isinstance(v, bytes) else str(v or "")
        with self._lock:
            wanted = set(self._wanted)
        for name in wanted - self._reqs.keys():
            self._reqs[name] = Request((f"L:{name}".encode(), b"Number"), self._sm, _time=0)
            self.names.add(name)
        vals: dict[str, float] = {}
        for name, req in self._reqs.items():
            val = req.value
            if val is not None:
                vals[name] = float(val)
        with self._lock:
            self.values = vals
        self.last_update = time.time()
