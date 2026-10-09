#!/usr/bin/env python3
"""Cycle a DU through every display and record what it costs.

    python tools/du_cycle_test.py [--serial <hex>] [--dwell 10] [--port 8765]

For each display (and once with the DU unassigned) the DU is assigned through the DMC's API, left for `dwell`
seconds, and these are recorded: DMC CPU (percent of one core, measured over the dwell time), source rate,
capture rate, the DU's own fps / decode / draw / rx times, and the brightness it is driven at.
The original assignment is restored at the end.
"""
from __future__ import annotations

import argparse
import json
import time
import urllib.request

import psutil


def api(port: int, path: str, body: dict | None = None):
    req = urllib.request.Request(f"http://127.0.0.1:{port}{path}", method="POST" if body is not None else "GET",
                                 data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Content-Type": "application/json"})
    return json.load(urllib.request.urlopen(req, timeout=5))


def dmc_process(port: int = 8765) -> psutil.Process:
    """The process listening on the DMC's port: the .NET DMC (GlassLink.exe) or the Python one, however started (#53)."""
    for c in psutil.net_connections(kind="tcp"):
        if c.status == psutil.CONN_LISTEN and c.laddr and c.laddr.port == port and c.pid:
            return psutil.Process(c.pid)
    raise SystemExit(f"no DMC listening on port {port}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--serial")
    ap.add_argument("--dwell", type=float, default=10.0)
    ap.add_argument("--port", type=int, default=8765)
    a = ap.parse_args()

    st = api(a.port, "/status")
    serial = a.serial or next(s for s, m in st["modules"].items() if m.get("alive"))
    original = st["modules"][serial].get("display") or ""
    proc = dmc_process(a.port)
    print(f"DMC {st['version']} ({st.get('build')}) pid {proc.pid} process {st.get('process')}")
    print(f"DU {serial[:8]} fw {(st['modules'][serial].get('info') or {}).get('fw')}  originally on '{original}'\n")
    rows = []
    for name in [""] + list(st["displays"]):
        api(a.port, f"/modules/{serial}", {"display": name})
        time.sleep(3.0)                                  # let capture rates settle
        t0 = proc.cpu_times(); w0 = time.time()
        time.sleep(a.dwell)
        t1 = proc.cpu_times(); w1 = time.time()
        cpu = ((t1.user + t1.system) - (t0.user + t0.system)) / (w1 - w0) * 100
        s = api(a.port, "/status")
        m = s["modules"][serial]; ms = m.get("stats") or {}; b = m.get("brightness") or {}
        d = s["displays"].get(name, {})
        in_use = [n for n, x in s["displays"].items() if x.get("in_use")]
        rows.append((name or "(unassigned)", cpu, d.get("fps"), d.get("capture_fps"), ms.get("fps"), ms.get("decode_ms"),
                     ms.get("draw_ms"), ms.get("rx_ms"), b.get("sent"), ",".join(in_use) or "-"))
    api(a.port, f"/modules/{serial}", {"display": original})
    hdr = ("display", "DMC cpu%", "src fps", "capture", "DU fps", "decode ms", "draw ms", "rx ms", "bright%", "in use")
    print("{:13s} {:>8s} {:>8s} {:>8s} {:>7s} {:>10s} {:>8s} {:>6s} {:>8s}  {}".format(*hdr))
    for r in rows:
        print("{:13s} {:8.1f} {:>8} {:>8} {:>7} {:>10} {:>8} {:>6} {:>8}  {}".format(
            r[0], r[1], *[("-" if v is None else v) for v in r[2:]]))
    print(f"\nrestored assignment: '{original}'")


if __name__ == "__main__":
    main()
