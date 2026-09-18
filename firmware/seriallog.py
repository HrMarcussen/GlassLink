#!/usr/bin/env python3
"""Append the board's serial output with timestamps to a file (no reset).  python seriallog.py COM3 3600 ../logs/module-console.log"""
import sys, time, serial
port, secs, path = sys.argv[1], float(sys.argv[2]), sys.argv[3]
s = serial.Serial(port, 115200, timeout=0.5)
out = open(path, "a", buffering=1, encoding="utf-8")
t0 = time.time(); buf = b""
while time.time() - t0 < secs:
    buf += s.read(4096)
    while b"\n" in buf:
        line, buf = buf.split(b"\n", 1)
        if line.strip():
            out.write(time.strftime("%H:%M:%S ") + line.decode("utf-8", "replace").rstrip() + "\n")
