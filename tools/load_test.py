#!/usr/bin/env python3
"""What does the DMC cost with N displays in use at once? No DUs needed.

    python tools/load_test.py [--dwell 30] [--port 8765]

A display is captured at full rate only while something uses it, so the test attaches one MJPEG reader per display
(/stream/<name>), which is the same work as feeding a DU: capture, change detection, JPEG encode, one consumer.
Rounds: nothing in use, then 1, 2, ... all displays in use. Per round: DMC CPU in percent of one core (and of the
whole machine), its memory, and each display's source and capture rate. The sim's frame rate is not measured here:
watch it in the sim while the last round runs.
"""
from __future__ import annotations

import argparse
import threading
import time
import urllib.request

import psutil

from du_cycle_test import api, dmc_process


class Reader(threading.Thread):
    """Reads an MJPEG stream and throws it away; counts bytes."""

    def __init__(self, port: int, name: str) -> None:
        super().__init__(daemon=True)
        self.url, self.name_, self.bytes, self.stop = f"http://127.0.0.1:{port}/stream/{name}", name, 0, False

    def run(self) -> None:
        try:
            # a display whose picture does not change sends nothing, for minutes: no read timeout
            with urllib.request.urlopen(self.url, timeout=3600) as r:
                while not self.stop:
                    chunk = r.read(65536)
                    if not chunk:
                        break
                    self.bytes += len(chunk)
        except Exception as exc:  # noqa: BLE001
            if not self.stop:
                print(f"  reader {self.name_}: {exc}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dwell", type=float, default=30.0)
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--all", type=int, default=0, metavar="N",
                    help="skip the ramp: all displays in use at once, N samples of --dwell seconds (e.g. during a takeoff)")
    a = ap.parse_args()

    st = api(a.port, "/status")
    names = [n for n, d in st["displays"].items() if d["window"].get("hwnd")]
    if not names:
        raise SystemExit("no display has a window (is the sim in the cockpit?)")
    proc = dmc_process()
    ncpu = psutil.cpu_count()
    print(f"DMC {st['version']} ({st.get('build')}) pid {proc.pid} process {st.get('process')}; {ncpu} logical CPUs")
    print(f"displays with a window: {names}\n")
    print("{:>7s} {:>10s} {:>10s} {:>8s} {:>9s}  {}".format("in use", "DMC core%", "machine%", "RSS MB", "MB/s out", "source fps / capture fps per display"))

    readers: list[Reader] = []
    try:
        for n_use in ([len(names)] * a.all if a.all else range(0, len(names) + 1)):
            while len(readers) < n_use:
                r = Reader(a.port, names[len(readers)])
                r.start()
                readers.append(r)
            if len(readers) and readers[-1].bytes == 0 or not a.all:
                time.sleep(6.0)                          # capture rates settle (idle -> full takes a moment)
            b0 = sum(r.bytes for r in readers)
            t0 = proc.cpu_times(); w0 = time.time()
            psutil.cpu_percent(None)
            time.sleep(a.dwell)
            machine = psutil.cpu_percent(None)
            t1 = proc.cpu_times(); w1 = time.time()
            cpu = ((t1.user + t1.system) - (t0.user + t0.system)) / (w1 - w0) * 100
            mbs = (sum(r.bytes for r in readers) - b0) / (w1 - w0) / 1e6
            s = api(a.port, "/status")
            rates = "  ".join(f"{n}:{s['displays'][n].get('fps')}/{s['displays'][n].get('capture_fps')}" for n in names)
            print("{:>7d} {:>10.1f} {:>10.1f} {:>8.0f} {:>9.2f}  {}".format(
                n_use, cpu, machine, proc.memory_info().rss / 1e6, mbs, rates))
    finally:
        for r in readers:
            r.stop = True
    print("\nDMC core% = percent of ONE logical CPU. The DMC is pinned to the CPUs shown above, away from the sim's.")


if __name__ == "__main__":
    main()
