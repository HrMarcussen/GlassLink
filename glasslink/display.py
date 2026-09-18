"""DisplayWorker: finds the window for one display, runs a capture backend, crops, encodes, publishes."""

from __future__ import annotations

import logging
import threading
import time
from typing import Any

import numpy as np

from . import windows as win
from .capture import create_backend
from .capture.base import CaptureBackend
from .encoder import crop_view, downscale, encode_jpeg, frames_equal
from .hub import FrameHub

log = logging.getLogger(__name__)


class DisplayWorker:
    def __init__(self, name: str, dcfg: dict[str, Any], global_cfg: dict[str, Any], hub: FrameHub) -> None:
        self.name = name
        self.dcfg = dcfg
        self.fps = float(dcfg.get("fps") or global_cfg["capture"]["fps"])
        self.quality = int(dcfg.get("quality") or global_cfg["capture"]["quality"])
        self.subsampling = str(global_cfg["capture"].get("subsampling", "420"))
        self.backend_name = str(global_cfg["capture"].get("backend", "auto"))
        self.hub = hub
        self.state = hub.add(name)

        self.hwnd: int | None = None
        self.backend: CaptureBackend | None = None
        self._lost = threading.Event()
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._supervise, name=f"display-{name}", daemon=True)

        self._lock = threading.Lock()
        self._last_emit = 0.0        # last published frame
        self._last_check = 0.0       # last frame that went through change detection (rate limit applies here)
        self._prev: np.ndarray | None = None   # BGRX copy of the last published picture
        self._crop_cache: tuple[tuple[int, int], tuple[int, int, int, int]] | None = None
        self._crop_logged = False
        self.counters = {"received": 0, "throttled": 0, "unchanged": 0, "published": 0}
        self.state.counters = self.counters

    # -- lifecycle ---------------------------------------------------------------------
    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        self._teardown_backend()
        log.info("[%s] capture stopped", self.name)

    def _supervise(self) -> None:
        while not self._stop.is_set():
            if self.backend is None or self._lost.is_set():
                self._teardown_backend()
                self._try_attach()
            elif self.hwnd is not None and not win.is_window(self.hwnd):
                log.info("[%s] window %s disappeared", self.name, hex(self.hwnd))
                self._lost.set()
                continue
            self._stop.wait(2.0)

    def _teardown_backend(self) -> None:
        b, self.backend = self.backend, None
        if b is not None:
            try:
                b.stop()
            except Exception:  # noqa: BLE001
                pass
        self._lost.clear()
        self._prev = None
        self._crop_cache = None
        self.hub.set_info_threadsafe(self.name, window_hwnd=None, backend="")

    def _try_attach(self) -> None:
        info = win.match_window(self.dcfg.get("match", {}))
        if info is None:
            self.hub.set_info_threadsafe(self.name, error="window not found")
            return
        if info.minimized:
            self.hub.set_info_threadsafe(self.name, error="window is minimised (restore it; it must not be minimised)")
            return
        self.hwnd = info.hwnd
        self._apply_geometry(info)
        try:
            self.backend = create_backend(self.backend_name, info.hwnd, self._on_frame, self._on_closed, self.fps)
        except Exception as exc:  # noqa: BLE001
            log.exception("[%s] could not start capture backend", self.name)
            self.hub.set_info_threadsafe(self.name, error=f"capture start failed: {exc}")
            self.backend = None
            return
        log.info("[%s] capturing %s '%s' (%s, client %sx%s) with %s", self.name, hex(info.hwnd), info.title,
                 info.process, info.client.width, info.client.height, self.backend.name)
        self.hub.set_info_threadsafe(self.name, window_hwnd=info.hwnd, window_title=info.title,
                                     backend=self.backend.name, error="")

    def _apply_geometry(self, info: win.WindowInfo) -> None:
        size = self.dcfg.get("client_size")
        pos = self.dcfg.get("position")
        try:
            if size and list(info.client.size) != list(size):
                x, y = (pos or (None, None))
                win.set_client_size(info.hwnd, int(size[0]), int(size[1]), x, y)
                log.info("[%s] resized client area to %sx%s", self.name, size[0], size[1])
            elif pos and (info.rect.left, info.rect.top) != tuple(pos):
                win.move_window(info.hwnd, int(pos[0]), int(pos[1]))
        except Exception:  # noqa: BLE001
            log.exception("[%s] could not apply window geometry", self.name)

    # -- capture thread ----------------------------------------------------------------
    def _on_closed(self) -> None:
        log.info("[%s] capture session closed", self.name)
        self._lost.set()

    def _on_frame(self, frame: np.ndarray, ts: float) -> None:
        self.counters["received"] += 1
        if self.fps and ts - self._last_check < (1.0 / self.fps) * 0.9:
            self.counters["throttled"] += 1
            return
        self._last_check = ts
        h, w = frame.shape[:2]
        crop = self.dcfg.get("crop")
        if crop:
            crop_t = (int(crop[0]), int(crop[1]), int(crop[2]), int(crop[3]))
        else:
            if self._crop_cache is None or self._crop_cache[0] != (w, h):
                self._crop_cache = ((w, h), win.compute_crop(self.hwnd, w, h))
                if not self._crop_logged:
                    log.info("[%s] frame %sx%s, crop %s", self.name, w, h, self._crop_cache[1])
                    self._crop_logged = True
            crop_t = self._crop_cache[1]
        view = crop_view(frame, crop_t)                   # 4-channel view into the capture buffer, no copy
        if frames_equal(self._prev, view):
            self.counters["unchanged"] += 1
            return
        self.counters["published"] += 1
        bgr = np.ascontiguousarray(view)                  # one 0.5 ms copy, only for frames that changed
        self._prev = bgr
        max_size = self.dcfg.get("max_size")
        if max_size:
            bgr = downscale(bgr, int(max_size))
        jpeg = encode_jpeg(bgr, self.quality, self.subsampling)
        self._last_emit = ts
        self.hub.publish_threadsafe(self.name, jpeg, bgr.shape[1], bgr.shape[0], ts)
