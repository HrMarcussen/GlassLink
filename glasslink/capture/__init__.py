"""Capture backends. Each delivers full-window BGRA numpy frames on its own thread."""

from __future__ import annotations

from .base import CaptureBackend, FrameCallback

BACKENDS = ("wgc", "printwindow")


def create_backend(name: str, hwnd: int, on_frame: FrameCallback, on_closed, fps: float) -> CaptureBackend:
    """Create a backend by name; 'auto' tries wgc first and falls back to printwindow."""
    if name == "auto":
        try:
            from .wgc import WgcBackend

            b = WgcBackend(hwnd, on_frame, on_closed, fps)
            b.start()
            return b
        except Exception as exc:  # noqa: BLE001
            import logging

            logging.getLogger(__name__).warning("wgc backend failed (%s); falling back to printwindow", exc)
            name = "printwindow"
    if name == "wgc":
        from .wgc import WgcBackend

        b = WgcBackend(hwnd, on_frame, on_closed, fps)
    elif name == "printwindow":
        from .printwindow import PrintWindowBackend

        b = PrintWindowBackend(hwnd, on_frame, on_closed, fps)
    else:
        raise ValueError(f"unknown capture backend: {name}")
    b.start()
    return b
