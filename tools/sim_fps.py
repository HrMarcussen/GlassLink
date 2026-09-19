#!/usr/bin/env python3
"""Sim frame rate next to the pop-out frame rate, to see what limits the displays.

    python tools/sim_fps.py [--secs 60] [--display pfd] [--port 8765]

The sim's own frame rate comes from SimConnect's "Frame" system event (one event per rendered sim frame, with the
frame rate in it). The display's rate is the DMC's `received` counter: how often the pop-out window presented a
frame. One line every 3 s: sim fps (reported), sim frames counted, pop-out frames, ratio.
"""
from __future__ import annotations

import argparse
import ctypes
import json
import os
import struct
import time
import urllib.request
from ctypes import wintypes

import SimConnect as sc_pkg

RECV_ID_EVENT_FRAME = 7


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--secs", type=float, default=60.0)
    ap.add_argument("--display", default="pfd")
    ap.add_argument("--port", type=int, default=8765)
    a = ap.parse_args()

    dll = ctypes.WinDLL(os.path.join(os.path.dirname(sc_pkg.__file__), "SimConnect.dll"))
    h = wintypes.HANDLE()
    hr = dll.SimConnect_Open(ctypes.byref(h), b"GlassLink fps probe", None, 0, None, 0)
    if hr != 0:
        raise SystemExit(f"SimConnect_Open failed: {hr & 0xFFFFFFFF:#x}")
    dll.SimConnect_SubscribeToSystemEvent(h, 1, b"Frame")
    pdata, cb = ctypes.c_void_p(), wintypes.DWORD()

    def received() -> int:
        s = json.load(urllib.request.urlopen(f"http://127.0.0.1:{a.port}/status", timeout=5))
        return s["displays"][a.display]["counters"]["received"]

    print(f"{'time':8s} {'sim fps':>8s} {'counted':>8s} {a.display + ' fps':>10s} {'ratio':>6s}")
    end = time.time() + a.secs
    while time.time() < end:
        frames, last_rate, r0, t0 = 0, 0.0, received(), time.time()
        while time.time() - t0 < 3.0:
            if dll.SimConnect_GetNextDispatch(h, ctypes.byref(pdata), ctypes.byref(cb)) == 0 and cb.value >= 28:
                buf = ctypes.string_at(pdata.value, cb.value)
                if struct.unpack_from("<I", buf, 8)[0] == RECV_ID_EVENT_FRAME:
                    frames += 1
                    last_rate = struct.unpack_from("<f", buf, 24)[0]
            else:
                time.sleep(0.001)
        dt = time.time() - t0
        pop = (received() - r0) / dt
        u = ctypes.windll.user32
        title = ctypes.create_unicode_buffer(80)
        u.GetWindowTextW(u.GetForegroundWindow(), title, 80)
        print(f"{time.strftime('%H:%M:%S')} {last_rate:8.1f} {frames / dt:8.1f} {pop:10.1f} {(frames / dt) / pop if pop else 0:6.2f}  fg: {title.value[:40]}", flush=True)
    dll.SimConnect_Close(h)


if __name__ == "__main__":
    main()
