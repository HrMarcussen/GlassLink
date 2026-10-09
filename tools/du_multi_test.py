#!/usr/bin/env python3
"""Run all connected DUs at once and record what each one gets.

    python tools/du_multi_test.py [--dwell 20] [--port 8765]

Three rounds, each `dwell` seconds, with the DMC's CPU (percent of one core) measured over the round:
  different   every DU on its own display (DU 1 -> first display, DU 2 -> second, ...)
  same        all DUs on the first display (one capture, one encode, several consumers)
  swapped     the assignment of "different" rotated by one (reassignment under load)
Per DU: source rate of its display, frames per second sent and shown, decode / draw / transfer times, dropped
frames, stalls reported by the DMC's health check. The original assignments are restored at the end.
"""
from __future__ import annotations

import argparse
import time

from du_cycle_test import api, dmc_process


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dwell", type=float, default=20.0)
    ap.add_argument("--port", type=int, default=8765)
    a = ap.parse_args()

    st = api(a.port, "/status")
    serials = [s for s, m in st["modules"].items() if m.get("alive")]
    displays = [n for n, d in st["displays"].items() if d["window"].get("hwnd")]
    if len(serials) < 2:
        raise SystemExit(f"need at least two connected DUs, found {len(serials)}")
    if len(displays) < len(serials):
        raise SystemExit(f"need {len(serials)} displays with a window, found {displays}")
    original = {s: st["modules"][s].get("display") or "" for s in serials}
    proc = dmc_process(a.port)
    print(f"DMC {st['version']} ({st.get('build')}) pid {proc.pid}; DUs: "
          + ", ".join(f"{s[:8]} '{st['modules'][s].get('label') or ''}' fw {(st['modules'][s].get('info') or {}).get('fw')}"
                      for s in serials))

    n = len(serials)
    rounds = {
        "different": {s: displays[i] for i, s in enumerate(serials)},
        "same": {s: displays[0] for s in serials},
        "swapped": {s: displays[(i + 1) % n] for i, s in enumerate(serials)},
    }
    hdr = ("round", "DU", "display", "src fps", "DU fps", "decode", "draw", "rx ms", "dropped")
    print("\n{:10s} {:9s} {:12s} {:>8s} {:>7s} {:>7s} {:>6s} {:>6s} {:>8s}".format(*hdr))
    try:
        for rname, plan in rounds.items():
            for s, disp in plan.items():
                api(a.port, f"/modules/{s}", {"display": disp})
            time.sleep(4.0)                                   # capture rates and the DUs settle
            before = api(a.port, "/status")
            t0 = proc.cpu_times(); w0 = time.time()
            time.sleep(a.dwell)
            t1 = proc.cpu_times(); w1 = time.time()
            cpu = ((t1.user + t1.system) - (t0.user + t0.system)) / (w1 - w0) * 100
            now = api(a.port, "/status")
            for s, disp in plan.items():
                m = now["modules"][s]; ms = m.get("stats") or {}
                d0 = ((before["modules"][s].get("stats") or {}).get("dropped")) or 0
                print("{:10s} {:9s} {:12s} {:>8} {:>7} {:>7} {:>6} {:>6} {:>8}".format(
                    rname, s[:8], disp, now["displays"][disp].get("fps"), ms.get("fps", "-"), ms.get("decode_ms", "-"),
                    ms.get("draw_ms", "-"), ms.get("rx_ms", "-"), (ms.get("dropped") or 0) - d0))
            print(f"{rname:10s} DMC {cpu:.1f}% of one core\n")
    finally:
        for s, disp in original.items():
            api(a.port, f"/modules/{s}", {"display": disp})
        print(f"restored assignments: {original}")


if __name__ == "__main__":
    main()
