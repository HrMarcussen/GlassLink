#!/usr/bin/env python3
"""How often does the PICTURE change? Pop-out windows present at a steady rate even when nothing on them moves;
what a DU shows is the rate of changed frames (`published`). Needs movement: taxi, fly, or turn a knob.

    python tools/content_fps.py [--secs 120] [--port 8765]

Every 5 s: frames presented / frames that differed, per display that is in use, and what the DUs report.
"""
from __future__ import annotations

import argparse
import time

from du_cycle_test import api


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--secs", type=float, default=120.0)
    ap.add_argument("--port", type=int, default=8765)
    a = ap.parse_args()
    end = time.time() + a.secs
    prev, t0 = api(a.port, "/status"), time.time()
    while time.time() < end:
        time.sleep(5.0)
        cur, t1 = api(a.port, "/status"), time.time()
        parts = []
        for n, d in cur["displays"].items():
            if not d.get("in_use"):
                continue
            c0, c1 = prev["displays"][n]["counters"], d["counters"]
            parts.append(f"{n} {(c1['received'] - c0['received']) / (t1 - t0):4.1f}/{(c1['published'] - c0['published']) / (t1 - t0):4.1f}")
        dus = [f"{m.get('label') or s[:6]} {(m.get('stats') or {}).get('fps')}" for s, m in cur["modules"].items() if m.get("alive")]
        print(f"{time.strftime('%H:%M:%S')}  presented/changed: {'  '.join(parts) or '-'}   DU fps: {', '.join(dus)}", flush=True)
        prev, t0 = cur, t1


if __name__ == "__main__":
    main()
