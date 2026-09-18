from __future__ import annotations

from typing import Callable

import numpy as np

# on_frame(bgra_frame, timestamp_monotonic). The array is only valid during the call
# (zero-copy views for wgc); copy what you need to keep.
FrameCallback = Callable[[np.ndarray, float], None]
ClosedCallback = Callable[[], None]


class CaptureBackend:
    name = "base"

    def __init__(self, hwnd: int, on_frame: FrameCallback, on_closed: ClosedCallback, fps: float) -> None:
        self.hwnd = hwnd
        self.on_frame = on_frame
        self.on_closed = on_closed
        self.fps = fps

    def start(self) -> None:
        raise NotImplementedError

    def stop(self) -> None:
        raise NotImplementedError
