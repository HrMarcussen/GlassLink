"""Windows.Graphics.Capture backend via the `windows-capture` package.

Frames only arrive when the window presents (a static window produces no callbacks).
Occluded and partly off-screen windows are captured; minimised windows are not.
"""

from __future__ import annotations

import logging
import time

from windows_capture import WindowsCapture

from .base import CaptureBackend, ClosedCallback, FrameCallback

log = logging.getLogger(__name__)


class WgcBackend(CaptureBackend):
    name = "wgc"

    def __init__(self, hwnd: int, on_frame: FrameCallback, on_closed: ClosedCallback, fps: float) -> None:
        super().__init__(hwnd, on_frame, on_closed, fps)
        self._control = None
        self._closed_sent = False
        self._capture = self._make_capture(use_interval=True)

    def _make_capture(self, use_interval: bool) -> WindowsCapture:
        kwargs = dict(cursor_capture=False, draw_border=False, window_hwnd=self.hwnd)
        if use_interval and self.fps:
            kwargs["minimum_update_interval"] = max(1, int(1000 / self.fps))
        try:
            cap = WindowsCapture(**kwargs)
        except Exception:
            if not use_interval:
                raise
            log.info("minimum_update_interval not supported here; capturing at native rate")
            return self._make_capture(use_interval=False)

        backend = self

        @cap.event
        def on_frame_arrived(frame, control):  # noqa: ANN001 - signature fixed by library
            try:
                backend.on_frame(frame.frame_buffer, time.monotonic())
            except Exception:  # noqa: BLE001
                log.exception("frame handler failed")

        @cap.event
        def on_closed():
            backend._emit_closed()

        return cap

    def _emit_closed(self) -> None:
        if not self._closed_sent:
            self._closed_sent = True
            try:
                self.on_closed()
            except Exception:  # noqa: BLE001
                log.exception("closed handler failed")

    def start(self) -> None:
        self._control = self._capture.start_free_threaded()

    def stop(self) -> None:
        ctl, self._control = self._control, None
        if ctl is not None:
            try:
                ctl.stop()
                # wait (bounded) for the capture thread to close the session cleanly
                for _ in range(20):
                    if ctl.is_finished():
                        break
                    time.sleep(0.05)
            except Exception:  # noqa: BLE001
                pass
