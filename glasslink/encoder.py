"""Crop, optional downscale, change detection and JPEG encoding."""

from __future__ import annotations

import numpy as np
import simplejpeg


def crop_view(frame: np.ndarray, crop: tuple[int, int, int, int]) -> np.ndarray:
    x, y, w, h = crop
    return frame[y : y + h, x : x + w]


def downscale(bgr: np.ndarray, max_size: int) -> np.ndarray:
    h, w = bgr.shape[:2]
    m = max(w, h)
    if m <= max_size:
        return bgr
    import cv2  # provided by windows-capture's opencv dependency

    scale = max_size / m
    # whole 16-pixel blocks: the DU's JPEG decoder refuses 4:2:0 pictures of other sizes
    size = (max(16, round(w * scale / 16) * 16), max(16, round(h * scale / 16) * 16))
    return cv2.resize(bgr, size, interpolation=cv2.INTER_AREA)


def frames_equal(a: np.ndarray | None, b: np.ndarray) -> bool:
    """True if the pictures are identical. `b` may be a row-strided view of the capture buffer (keep all
    4 channels so each row stays contiguous: 0.9 ms for 768x768 instead of 2.8 ms for a 3-channel view).
    A coarse sample (every 16th row, 0.1 ms) goes first, so a changed frame is detected almost for free
    and only a frame that passes the sample gets the full compare."""
    if a is None or a.shape != b.shape:
        return False
    if not np.array_equal(a[::16], b[::16]):
        return False
    return np.array_equal(a, b)


def encode_jpeg(bgr: np.ndarray, quality: int = 85, subsampling: str = "420") -> bytes:
    """Encode a BGR or BGRX (capture buffer, alpha ignored) picture; 4-channel input avoids a 3-channel copy."""
    if not bgr.flags["C_CONTIGUOUS"]:
        bgr = np.ascontiguousarray(bgr)
    cs = "BGRX" if bgr.shape[2] == 4 else "BGR"
    return simplejpeg.encode_jpeg(bgr, quality=int(quality), colorspace=cs, colorsubsampling=subsampling, fastdct=True)


