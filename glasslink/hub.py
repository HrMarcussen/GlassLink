"""Latest-frame hub: one slot per display, bridged from capture threads into asyncio."""

from __future__ import annotations

import asyncio
import threading
import time
from collections import deque
from dataclasses import dataclass, field
from typing import Any


@dataclass
class DisplayState:
    name: str
    seq: int = 0
    jpeg: bytes | None = None
    width: int = 0
    height: int = 0
    ts: float = 0.0                 # monotonic time of the last published frame
    window_hwnd: int | None = None
    window_title: str = ""
    backend: str = ""
    error: str = ""
    clients: int = 0                # connected LAN viewers (WebSocket / MJPEG)
    du_assigned: int = 0            # connected DUs showing this display (maintained by the module manager)
    capture_fps: float = 0.0        # rate the capture session currently runs at (full or idle)
    counters: dict | None = None
    cond: asyncio.Condition = field(default_factory=asyncio.Condition)
    tevent: threading.Event = field(default_factory=threading.Event)   # legacy; USB modules use tcond
    tcond: threading.Condition = field(default_factory=threading.Condition)   # for non-asyncio consumers
    _times: deque = field(default_factory=lambda: deque(maxlen=120))
    bytes_out: int = 0

    def latest(self) -> tuple[int, bytes | None]:
        """Thread-safe snapshot of the newest frame (seq, jpeg)."""
        return self.seq, self.jpeg

    def fps(self) -> float:
        now = time.monotonic()
        recent = [t for t in self._times if now - t <= 2.0]
        return round(len(recent) / 2.0, 1)

    def as_dict(self) -> dict[str, Any]:
        return {
            "seq": self.seq,
            "size": [self.width, self.height],
            "fps": self.fps(),
            "last_frame_age_s": round(time.monotonic() - self.ts, 2) if self.ts else None,
            "jpeg_bytes": len(self.jpeg) if self.jpeg else 0,
            "window": {"hwnd": hex(self.window_hwnd) if self.window_hwnd else None, "title": self.window_title},
            "backend": self.backend,
            "error": self.error,
            "clients": self.clients,
            "du_assigned": self.du_assigned,
            "capture_fps": self.capture_fps,
            "in_use": self.clients > 0 or self.du_assigned > 0,
            "counters": dict(self.counters) if self.counters else {},
        }


class FrameHub:
    def __init__(self, loop: asyncio.AbstractEventLoop) -> None:
        self.loop = loop
        self.displays: dict[str, DisplayState] = {}

    def add(self, name: str) -> DisplayState:
        st = DisplayState(name=name)
        self.displays[name] = st
        return st

    def get(self, name: str) -> DisplayState | None:
        return self.displays.get(name)

    def remove(self, name: str) -> None:
        """Forget a display (the display editor removed it). Late frames from its capture thread are ignored."""
        self.displays.pop(name, None)

    # -- called from capture threads -------------------------------------------------
    def publish_threadsafe(self, name: str, jpeg: bytes, width: int, height: int, ts: float) -> None:
        self.loop.call_soon_threadsafe(self._publish, name, jpeg, width, height, ts)

    def set_info_threadsafe(self, name: str, **info: Any) -> None:
        self.loop.call_soon_threadsafe(self._set_info, name, info)

    # -- loop thread -------------------------------------------------------------------
    def _publish(self, name: str, jpeg: bytes, width: int, height: int, ts: float) -> None:
        st = self.displays.get(name)
        if st is None:
            return
        with st.tcond:
            st.seq += 1
            st.jpeg = jpeg
            st.width, st.height = width, height
            st.ts = ts
            st._times.append(time.monotonic())
            st.tcond.notify_all()
        st.tevent.set()
        asyncio.ensure_future(self._notify(st))

    def _set_info(self, name: str, info: dict[str, Any]) -> None:
        st = self.displays.get(name)
        if st is None:
            return
        for k, v in info.items():
            setattr(st, k, v)

    async def _notify(self, st: DisplayState) -> None:
        async with st.cond:
            st.cond.notify_all()

    async def wait_newer(self, st: DisplayState, last_seq: int, timeout: float = 1.0) -> tuple[int, bytes] | None:
        """Return (seq, jpeg) once a frame newer than last_seq exists, or None on timeout."""
        if st.jpeg is not None and st.seq > last_seq:
            return st.seq, st.jpeg
        async with st.cond:
            try:
                await asyncio.wait_for(st.cond.wait_for(lambda: st.jpeg is not None and st.seq > last_seq), timeout)
            except asyncio.TimeoutError:
                return None
        return st.seq, st.jpeg

    def status(self) -> dict[str, Any]:
        return {name: st.as_dict() for name, st in self.displays.items()}
