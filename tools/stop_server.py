#!/usr/bin/env python3
"""Stop a running GlassLink DMC gracefully (never kill it: it holds capture sessions on the sim).

    python tools/stop_server.py [--port 8765] [--pid N]

Asks the DMC with POST /shutdown. If that fails, runs the DMC's own `GlassLink.exe --quit` (the exe of the process
that listens on the port). Returns when the DMC process has ended, not only its port: the captures are released last.
"""
import argparse
import json
import subprocess
import sys
import time
import urllib.request


def port_open(port: int) -> bool:
    try:
        urllib.request.urlopen(f"http://127.0.0.1:{port}/status", timeout=2)
        return True
    except Exception:  # noqa: BLE001
        return False


def find_pid(port: int) -> int | None:
    """The process that listens on the port (needs psutil); else the one GlassLink.exe that runs."""
    try:
        import psutil
        for c in psutil.net_connections(kind="tcp"):
            if c.status == psutil.CONN_LISTEN and c.laddr and c.laddr.port == port and c.pid:
                return c.pid
    except Exception:  # noqa: BLE001
        pass
    out = subprocess.run(["powershell", "-NoProfile", "-Command", "(Get-Process GlassLink -ErrorAction SilentlyContinue).Id"],
                         capture_output=True, text=True).stdout.split()
    return int(out[0]) if len(out) == 1 else None


def exe_of(pid: int) -> str | None:
    out = subprocess.run(["powershell", "-NoProfile", "-Command", f"(Get-Process -Id {pid} -ErrorAction SilentlyContinue).Path"],
                         capture_output=True, text=True).stdout.strip()
    return out or None


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--pid", type=int)
    a = ap.parse_args()
    if not port_open(a.port):
        print("no DMC on port", a.port)
        return
    pid = a.pid or find_pid(a.port)          # while the port is still open: afterwards nothing names it
    try:
        r = urllib.request.urlopen(urllib.request.Request(f"http://127.0.0.1:{a.port}/shutdown", method="POST", data=b"{}",
                                                          headers={"Content-Type": "application/json"}), timeout=3)
        print("shutdown endpoint:", json.loads(r.read() or b"{}"), flush=True)
    except Exception as exc:  # noqa: BLE001
        exe = exe_of(pid) if pid else None
        if not exe:
            raise SystemExit(f"no /shutdown ({exc}) and the DMC's GlassLink.exe was not found; quit it from its tray icon")
        print(f"no /shutdown ({exc}); running {exe} --quit")
        subprocess.run([exe, "--quit", "--port", str(a.port)], timeout=60)
    # The port closes before the captures are released: wait for the process itself, so a script that starts another
    # DMC next cannot overlap capture sessions (#53).
    for _ in range(60):
        time.sleep(0.5)
        if not port_open(a.port) and (not pid or not pid_alive(pid)):
            print("DMC stopped" if pid else "port closed (the DMC's process could not be named: give it a few seconds)")
            return
    print("DMC still running after 30 s", file=sys.stderr)
    sys.exit(1)


def pid_alive(pid: int) -> bool:
    import ctypes
    handle = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)        # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return False
    code = ctypes.c_ulong()
    ctypes.windll.kernel32.GetExitCodeProcess(handle, ctypes.byref(code))
    ctypes.windll.kernel32.CloseHandle(handle)
    return code.value == 259                                                # STILL_ACTIVE


if __name__ == "__main__":
    main()
