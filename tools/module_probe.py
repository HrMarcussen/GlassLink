#!/usr/bin/env python3
"""Talk to a connected GlassLink USB module directly (no server): info, ping, ident, and a test frame.

    python tools/module_probe.py                 # list modules, GET_INFO, PING, STATS
    python tools/module_probe.py --ident 5       # flash the identify screen for 5 s
    python tools/module_probe.py --frames 60     # stream 60 synthetic 768x768 JPEG frames and report fps/decode time
    python tools/module_probe.py --image x.jpg   # show one JPEG file
"""

from __future__ import annotations

import argparse
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from glasslink import modules as m  # noqa: E402


def make_test_jpeg(i: int, size: int = 768, quality: int = 85) -> bytes:
    import numpy as np
    import simplejpeg

    img = np.zeros((size, size, 3), np.uint8)
    img[:, :, 0] = (i * 4) % 256                       # slowly changing red level
    x = int((0.5 + 0.45 * np.sin(i / 10)) * size)      # moving green bar
    img[:, max(0, x - 8):x + 8, 1] = 255
    img[size // 2 - 3:size // 2 + 3, :, :] = 255       # white horizon
    return simplejpeg.encode_jpeg(img, quality=quality, colorspace="BGR", colorsubsampling="420", fastdct=True)


def read_msgs(t: m.UsbTransport, reader: m.MessageReader, timeout_ms: int) -> list[m.Message]:
    chunk = t.read_chunk(timeout_ms)
    return reader.feed(chunk) if chunk else []


def wait_for(t, reader, msg_type, timeout_s=2.0):
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        for msg in read_msgs(t, reader, 200):
            print("  <-", msg, msg.json() if msg.type in (m.T_INFO, m.T_STATS) else (msg.payload[:60] if msg.type == m.T_LOG else ""))
            if msg.type == msg_type:
                return msg
    return None


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ident", type=int, metavar="SECONDS")
    ap.add_argument("--frames", type=int, default=0, help="stream N synthetic frames")
    ap.add_argument("--image", type=Path, help="JPEG file to show once")
    ap.add_argument("--quality", type=int, default=85)
    args = ap.parse_args()

    devs = m.find_usb_modules()
    print(f"modules found: {len(devs)}")
    if not devs:
        sys.exit(1)
    t = m.UsbTransport(devs[0])
    print("opened:", t.description, "serial", t.serial)
    reader = m.MessageReader()
    # drain anything pending (e.g. an earlier READY)
    for msg in read_msgs(t, reader, 300):
        print("  <- (pending)", msg)

    print("-> GET_INFO"); t.write(m.pack(m.T_GET_INFO), 1000)
    info = wait_for(t, reader, m.T_INFO)
    print("-> PING"); t.write(m.pack(m.T_PING, arg=0x1234), 1000)
    pong = wait_for(t, reader, m.T_PONG)
    print("pong ok:", bool(pong and pong.arg == 0x1234))

    if args.ident is not None:
        print(f"-> SHOW_IDENT {args.ident}s"); t.write(m.pack(m.T_SHOW_IDENT, arg=args.ident), 1000)

    if args.image:
        data = args.image.read_bytes()
        print(f"-> FRAME {len(data)} bytes from {args.image}")
        t.write(m.pack(m.T_FRAME, data, seq=1), 3000)
        wait_for(t, reader, m.T_READY, 5)

    if args.frames:
        sent = 0; t0 = time.time(); last_seq = 0
        # The module sends READY when it is ready for the next frame; honour it (latest-only flow control).
        t.write(m.pack(m.T_PING, arg=1), 1000)
        pending_ready = True  # assume we may send the first frame now
        while sent < args.frames and time.time() - t0 < 60:
            if pending_ready:
                jpeg = make_test_jpeg(sent, quality=args.quality)
                sent += 1
                t.write(m.pack(m.T_FRAME, jpeg, seq=sent), 3000)
                pending_ready = False
            for msg in read_msgs(t, reader, 500):
                if msg.type == m.T_READY:
                    last_seq = msg.seq; pending_ready = True
                elif msg.type == m.T_STATS:
                    print("  <- STATS", msg.json())
                elif msg.type == m.T_LOG:
                    print("  <- LOG", msg.payload.decode("utf-8", "replace"))
        dt = time.time() - t0
        print(f"sent {sent} frames in {dt:.1f} s = {sent / dt:.1f} fps (last READY seq {last_seq}, jpeg ~{len(make_test_jpeg(0, quality=args.quality)) // 1024} KB)")
        wait_for(t, reader, m.T_STATS, 3)
    t.close()


if __name__ == "__main__":
    main()
