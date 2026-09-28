#!/usr/bin/env python3
"""Stop a running GlassLink server gracefully (never kill it: it holds capture sessions on the sim).

    python tools/stop_server.py [--port 8765] [--pid N]

Tries POST /shutdown first. For servers without that route, attaches to the server's console and sends it
Ctrl+Break (the --pid is found automatically from the command line "glasslink ... serve" if not given).
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


def find_pid(port: int | None = None, fallback: bool = True) -> int | None:
    """The DMC's process: whatever listens on the port (the .NET or the Python DMC); else a Python DMC by its command line."""
    if port:
        try:
            import psutil
            for c in psutil.net_connections(kind="tcp"):
                if c.status == psutil.CONN_LISTEN and c.laddr and c.laddr.port == port and c.pid:
                    return c.pid
        except Exception:  # noqa: BLE001
            pass
    if not fallback:
        return None
    out = subprocess.run(["powershell", "-NoProfile", "-Command",
                          "Get-CimInstance Win32_Process | Where-Object { $_.Name -like 'python*' -and $_.CommandLine -like '*glasslink*serve*' } | Select-Object -ExpandProperty ProcessId"],
                         capture_output=True, text=True).stdout.split()
    return int(out[0]) if out else None


def ctrl_c(pid: int) -> None:
    import ctypes
    k = ctypes.windll.kernel32
    k.FreeConsole()
    if not k.AttachConsole(pid):
        raise SystemExit(f"AttachConsole({pid}) failed: {ctypes.get_last_error()}")
    k.SetConsoleCtrlHandler(None, True)      # don't kill ourselves
    # CTRL_BREAK (1): Ctrl+C was not delivered to a server started from a background shell, Ctrl+Break is.
    # The server's console handler stops the capture sessions before the process ends.
    if not k.GenerateConsoleCtrlEvent(1, 0):
        raise SystemExit("GenerateConsoleCtrlEvent failed")
    k.FreeConsole()


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--pid", type=int)
    a = ap.parse_args()
    if not port_open(a.port):
        print("no server on port", a.port)
        return
    listener = a.pid or find_pid(a.port, fallback=False)   # while the port is still open: afterwards nothing names it
    try:
        r = urllib.request.urlopen(urllib.request.Request(f"http://127.0.0.1:{a.port}/shutdown", method="POST", data=b"{}",
                                                          headers={"Content-Type": "application/json"}), timeout=3)
        print("shutdown endpoint:", json.loads(r.read() or b"{}"), flush=True)
    except Exception as exc:  # noqa: BLE001
        pid = a.pid or find_pid(a.port)
        print(f"no /shutdown ({exc}); sending Ctrl+Break to pid {pid}")
        if not pid:
            raise SystemExit("server pid not found; pass --pid")
        ctrl_c(pid)
    # The port closes before the captures are released: wait for the process itself, so a script that starts the
    # other DMC next cannot overlap capture sessions (#53).
    pid = listener
    for _ in range(60):
        time.sleep(0.5)
        if not port_open(a.port) and (not pid or not pid_alive(pid)):
            print("server stopped")
            return
    print("server still running after 30 s", file=sys.stderr)
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
