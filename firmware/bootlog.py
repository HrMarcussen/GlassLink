#!/usr/bin/env python3
"""Reset the board through the CH343 auto-reset lines and print its serial output for N seconds.

    python bootlog.py COM3 30 [--no-reset]
Run with the IDF Python environment (has pyserial), e.g. C:\\Espressif\\python_env\\idf5.5_py3.11_env\\Scripts\\python.exe
"""
import sys
import time

import serial

port = sys.argv[1] if len(sys.argv) > 1 else "COM3"
secs = float(sys.argv[2]) if len(sys.argv) > 2 else 30
s = serial.Serial(port, 115200, timeout=0.2)
if "--no-reset" not in sys.argv:
    s.dtr = False
    s.rts = True
    time.sleep(0.1)
    s.rts = False
t0 = time.time()
buf = b""
while time.time() - t0 < secs:
    buf += s.read(4096)
s.close()
lines = [l for l in buf.decode("utf-8", "replace").splitlines() if l.strip()]
print("\n".join(lines[-int(sys.argv[3]) if len(sys.argv) > 3 and sys.argv[3].isdigit() else -80:]))
