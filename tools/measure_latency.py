#!/usr/bin/env python3
"""Measure end-to-end software latency of a stream of tools/test_pattern.py.

The pattern encodes the wall clock (ms since epoch mod 2^20) as 20 black/white cells along its bottom
edge. This client receives frames over the WebSocket, decodes the stamp and compares it with its own
clock, so it must run on the same machine as the pattern (or on one with a synchronised clock).
Measured: pattern draw -> DWM -> Windows.Graphics.Capture -> crop/JPEG -> WebSocket -> client decode.
Not included: the monitor's own scan-out and the viewer's paint, typically one more frame.

    python tools/measure_latency.py --url ws://127.0.0.1:8765/ws/pattern --seconds 10
"""

from __future__ import annotations

import argparse
import asyncio
import statistics
import struct
import time

import numpy as np
import simplejpeg
import websockets

HEADER = struct.Struct("<II")


def decode_stamp(rgb: np.ndarray) -> int:
    h, w = rgb.shape[:2]
    cell = w // 20
    row = rgb[h - 8, :, :].astype(int).sum(axis=1)  # a row inside the 16 px strip
    stamp = 0
    for i in range(20):
        seg = row[i * cell + cell // 4 : i * cell + 3 * cell // 4]
        bit = 1 if seg.mean() > 3 * 128 else 0
        stamp = (stamp << 1) | bit
    return stamp


async def run(url: str, seconds: float) -> None:
    lat: list[float] = []
    sizes: list[int] = []
    t_end = time.time() + seconds
    async with websockets.connect(url, max_size=None) as ws:
        await ws.send("n")
        while time.time() < t_end:
            msg = await ws.recv()
            if not isinstance(msg, (bytes, bytearray)):
                continue
            now_ms = int(time.time() * 1000) & 0xFFFFF
            jpeg = bytes(msg[HEADER.size :])
            rgb = simplejpeg.decode_jpeg(jpeg, colorspace="RGB")
            stamp = decode_stamp(rgb)
            d = (now_ms - stamp) & 0xFFFFF
            if d < 5000:  # ignore garbage decodes
                lat.append(d)
                sizes.append(len(jpeg))
            await ws.send("n")
    if not lat:
        print("no frames decoded - is tools/test_pattern.py running and assigned to this display?")
        return
    lat.sort()
    print(f"frames: {len(lat)} in {seconds:.0f} s -> {len(lat) / seconds:.1f} fps, "
          f"{statistics.mean(sizes) / 1024:.0f} KB/frame, {statistics.mean(sizes) * len(lat) / seconds * 8 / 1e6:.1f} Mbit/s")
    print(f"latency ms: min {lat[0]}  median {lat[len(lat) // 2]}  p90 {lat[int(len(lat) * 0.9)]}  max {lat[-1]}")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", default="ws://127.0.0.1:8765/ws/pattern")
    ap.add_argument("--seconds", type=float, default=10.0)
    args = ap.parse_args()
    asyncio.run(run(args.url, args.seconds))


if __name__ == "__main__":
    main()
