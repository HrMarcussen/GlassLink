"""Fallback backend: PrintWindow(PW_RENDERFULLCONTENT) on a timer thread.

Pure pywin32/ctypes, no extra dependencies. Captures occluded and off-screen DirectX windows
through DWM. Delivers the GetWindowRect-sized frame (title bar included) as BGRA.
"""

from __future__ import annotations

import ctypes
import logging
import threading
import time

import numpy as np
import win32gui
import win32ui

from .base import CaptureBackend, ClosedCallback, FrameCallback

log = logging.getLogger(__name__)

_PW_RENDERFULLCONTENT = 0x00000002
_user32 = ctypes.windll.user32
_user32.PrintWindow.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint]
_user32.PrintWindow.restype = ctypes.c_int


def grab_window(hwnd: int) -> np.ndarray | None:
    """Single PrintWindow grab -> (h, w, 4) BGRA array, or None if the window is gone/empty."""
    if not win32gui.IsWindow(hwnd):
        return None
    l, t, r, b = win32gui.GetWindowRect(hwnd)
    w, h = r - l, b - t
    if w <= 0 or h <= 0:
        return None
    hwnd_dc = win32gui.GetWindowDC(hwnd)
    src_dc = win32ui.CreateDCFromHandle(hwnd_dc)
    mem_dc = src_dc.CreateCompatibleDC()
    bmp = win32ui.CreateBitmap()
    try:
        bmp.CreateCompatibleBitmap(src_dc, w, h)
        mem_dc.SelectObject(bmp)
        ok = _user32.PrintWindow(hwnd, mem_dc.GetSafeHdc(), _PW_RENDERFULLCONTENT)
        if not ok:
            return None
        raw = bmp.GetBitmapBits(True)
        arr = np.frombuffer(raw, dtype=np.uint8).reshape(h, w, 4)
        return arr.copy()
    finally:
        try:
            win32gui.DeleteObject(bmp.GetHandle())
        except Exception:  # noqa: BLE001
            pass
        mem_dc.DeleteDC()
        src_dc.DeleteDC()
        win32gui.ReleaseDC(hwnd, hwnd_dc)


class PrintWindowBackend(CaptureBackend):
    name = "printwindow"

    def __init__(self, hwnd: int, on_frame: FrameCallback, on_closed: ClosedCallback, fps: float) -> None:
        super().__init__(hwnd, on_frame, on_closed, fps)
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    def start(self) -> None:
        self._thread = threading.Thread(target=self._run, name=f"printwindow-{self.hwnd:x}", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()

    def _run(self) -> None:
        interval = 1.0 / (self.fps or 20)
        failures = 0
        next_t = time.monotonic()
        while not self._stop.is_set():
            now = time.monotonic()
            if now < next_t:
                time.sleep(min(next_t - now, 0.05))
                continue
            next_t = now + interval
            try:
                frame = grab_window(self.hwnd)
            except Exception:  # noqa: BLE001
                log.exception("PrintWindow grab failed")
                frame = None
            if frame is None:
                failures += 1
                if not win32gui.IsWindow(self.hwnd) or failures > 3 * (self.fps or 20):
                    break
                continue
            failures = 0
            try:
                self.on_frame(frame, now)
            except Exception:  # noqa: BLE001
                log.exception("frame handler failed")
        if not self._stop.is_set():
            try:
                self.on_closed()
            except Exception:  # noqa: BLE001
                log.exception("closed handler failed")
