"""Command line: serve | list-windows | dump-windows | assign | snapshot | show-window."""

from __future__ import annotations

import argparse
import json
import logging
import sys
import threading
import time
from pathlib import Path

from . import windows as win
from .config import load_config, override, save_config

log = logging.getLogger("glasslink")

SIM_PROCESS = "FlightSimulator2024.exe"
SIM_CLASS = "AceApp"


def _print_windows(infos: list[win.WindowInfo]) -> None:
    infos = sorted(infos, key=lambda w: (w.process.lower(), w.cls, w.title))
    print(f"{'hwnd':>10} {'process':<26} {'class':<22} {'client':>11} {'vis':<4} {'min':<4} title")
    for w in infos:
        vis = "y" if w.visible else "-"
        mn = "y" if w.minimized else "-"
        print(f"{hex(w.hwnd):>10} {w.process[:26]:<26} {w.cls[:22]:<22} {w.client.width:>5}x{w.client.height:<5} {vis:<4} {mn:<4} {w.title[:70]}")


def cmd_list_windows(args: argparse.Namespace) -> int:
    infos = win.enum_windows(include_hidden=args.all, process=args.process)
    if args.json:
        print(json.dumps([w.as_dict() for w in infos], indent=2))
    else:
        _print_windows(infos)
    return 0


def cmd_dump_windows(args: argparse.Namespace) -> int:
    infos = win.enum_windows(include_hidden=True, process=args.process)
    if not infos:
        print(f"no windows found for process {args.process}")
        return 1
    for w in infos:
        print(json.dumps(w.as_dict()))
    return 0


def cmd_show_window(args: argparse.Namespace) -> int:
    hwnd = int(args.hwnd, 0)
    win.show_window_noactivate(hwnd)
    info = win.window_info(hwnd)
    print(json.dumps(info.as_dict() if info else None, indent=2))
    return 0


def cmd_assign(args: argparse.Namespace) -> int:
    cfg = load_config(args.config)
    name = args.name
    dcfg = cfg["displays"].get(name) or {}
    target: win.WindowInfo | None = None

    if args.hwnd:
        target = win.window_info(int(args.hwnd, 0))
    elif args.title:
        target = win.match_window({"title": args.title, "process": args.process} if args.process else {"title": args.title})
    else:
        process = args.process or SIM_PROCESS
        before = win.hwnd_set(process, SIM_CLASS if process == SIM_PROCESS else None)
        if not before and process == SIM_PROCESS:
            print(f"{process} is not running (no windows found).")
            return 1
        print(f"Watching {process} for a new window.")
        print(f"Pop out the '{name}' display in the sim now (right-alt + click on the display)…")
        deadline = time.monotonic() + args.timeout
        new: set[int] = set()
        while time.monotonic() < deadline:
            time.sleep(0.5)
            now = win.hwnd_set(process, SIM_CLASS if process == SIM_PROCESS else None)
            new = now - before
            if new:
                time.sleep(1.0)  # let the pop-out finish creating
                break
        if not new:
            print("No new window appeared. Timed out.")
            return 1
        if len(new) > 1:
            print(f"More than one new window appeared: {[hex(h) for h in new]} - use --hwnd to pick one.")
            return 1
        target = win.window_info(new.pop())

    if target is None:
        print("Window not found.")
        return 1

    title = f"GlassLink:{name}"
    if not args.no_rename:
        win.rename_window(target.hwnd, title)
        match = {"process": target.process, "class": target.cls, "title": title}
    else:
        match = {"process": target.process, "class": target.cls, "title": target.title}
    dcfg["match"] = match
    if args.size:
        dcfg["client_size"] = [max(64, (int(v) + 8) // 16 * 16) for v in args.size.lower().split("x")]   # whole 16-pixel blocks (the DU decoder)
    if args.position:
        dcfg["position"] = [int(v) for v in args.position.split(",")]
    if dcfg.get("client_size"):
        x, y = (dcfg.get("position") or (None, None))
        win.set_client_size(target.hwnd, dcfg["client_size"][0], dcfg["client_size"][1], x, y)
    elif dcfg.get("position"):
        win.move_window(target.hwnd, *dcfg["position"])
    cfg["displays"][name] = dcfg
    path = save_config(cfg, args.config)
    info = win.window_info(target.hwnd)
    print(f"Assigned '{name}' -> {hex(target.hwnd)} '{info.title if info else title}' "
          f"client {info.client.width if info else '?'}x{info.client.height if info else '?'}; saved {path}")
    return 0


def cmd_snapshot(args: argparse.Namespace) -> int:
    import cv2

    cfg = load_config(args.config)
    if args.name not in cfg["displays"]:
        print(f"unknown display '{args.name}' (configured: {', '.join(cfg['displays']) or 'none'})")
        return 1
    dcfg = cfg["displays"][args.name]
    info = win.match_window(dcfg["match"])
    if info is None:
        print("window not found for match rule", dcfg["match"])
        return 1
    print(f"window {hex(info.hwnd)} '{info.title}' rect={info.rect.as_list()} frame={info.frame.as_list()} "
          f"client={info.client.as_list()} minimized={info.minimized}")
    backend_name = args.backend or cfg["capture"]["backend"]
    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)

    got = threading.Event()
    holder: dict = {}

    def on_frame(frame, ts):  # noqa: ANN001
        if not got.is_set():
            holder["frame"] = frame.copy()
            got.set()

    def on_closed() -> None:
        holder.setdefault("closed", True)
        got.set()

    from .capture import create_backend

    backend = create_backend(backend_name, info.hwnd, on_frame, on_closed, 30)
    try:
        ok = got.wait(args.wait)
    finally:
        backend.stop()                       # also on Ctrl+C: never leave a capture session of the sim behind (#53)
    if not ok or "frame" not in holder:
        print("no frame received (window static and backend event-driven? try --backend printwindow, or wiggle the window)")
        return 1
    frame = holder["frame"]
    h, w = frame.shape[:2]
    crop = tuple(dcfg["crop"]) if dcfg.get("crop") else win.compute_crop(info.hwnd, w, h)
    x, y, cw, ch = crop
    raw_path = out_dir / f"{args.name}_raw_{w}x{h}.png"
    crop_path = out_dir / f"{args.name}_crop_{cw}x{ch}.png"
    cv2.imwrite(str(raw_path), frame[..., :3])
    cv2.imwrite(str(crop_path), frame[y : y + ch, x : x + cw, :3])
    print(f"backend={backend.name} frame={w}x{h} crop={crop}")
    print(f"wrote {raw_path}\nwrote {crop_path}")
    return 0


def cmd_serve(args: argparse.Namespace) -> int:
    cfg = load_config(args.config)
    cfg["_path"] = args.config
    # options on the command line are for this run only: never written into config.json (#49)
    if args.auto_popout is not None:
        override(cfg, "popout", "auto", args.auto_popout)
    if args.backend:
        override(cfg, "capture", "backend", args.backend)
    if args.port:
        override(cfg, "server", "port", args.port)
    if args.fake_modules:
        override(cfg, "usb", "fake_modules", args.fake_modules)
    from .server import serve

    serve(cfg)
    return 0


def main(argv: list[str] | None = None) -> None:
    p = argparse.ArgumentParser(prog="glasslink", description="Stream cockpit display windows over the LAN.")
    p.add_argument("-c", "--config", type=Path, default=None, help="config file (default: config.json)")
    p.add_argument("-v", "--verbose", action="store_true")
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("serve", help="run the display server")
    s.add_argument("--backend", choices=["auto", "wgc", "printwindow"])
    s.add_argument("--port", type=int)
    s.add_argument("--auto-popout", dest="auto_popout", action="store_true", default=None,
                   help="pop missing sim displays out automatically (default from config popout.auto)")
    s.add_argument("--no-auto-popout", dest="auto_popout", action="store_false")
    s.add_argument("--fake-modules", type=int, default=0, metavar="N", help="serve N in-process fake USB modules (UI development)")
    s.set_defaults(fn=cmd_serve)

    s = sub.add_parser("list-windows", help="list visible top-level windows")
    s.add_argument("--all", action="store_true", help="include hidden windows")
    s.add_argument("--process", help="filter by process name, e.g. FlightSimulator2024.exe")
    s.add_argument("--json", action="store_true")
    s.set_defaults(fn=cmd_list_windows)

    s = sub.add_parser("dump-windows", help="dump all windows (incl. hidden) of a process as JSON lines")
    s.add_argument("--process", required=True)
    s.set_defaults(fn=cmd_dump_windows)

    s = sub.add_parser("show-window", help="ShowWindow(SW_SHOWNOACTIVATE) on a hidden window")
    s.add_argument("--hwnd", required=True, help="e.g. 0x1a0b2c")
    s.set_defaults(fn=cmd_show_window)

    s = sub.add_parser("assign", help="assign a window to a display name (renames the window, saves config)")
    s.add_argument("name", help="display name, e.g. pfd, nd, ecam_upper, ecam_lower")
    s.add_argument("--hwnd", help="use this window instead of waiting for a new pop-out")
    s.add_argument("--title", help="use the window whose title contains this text")
    s.add_argument("--process", help=f"process to watch (default {SIM_PROCESS})")
    s.add_argument("--size", help="client size to apply, e.g. 768x768")
    s.add_argument("--position", help="window position to apply, e.g. 0,0")
    s.add_argument("--no-rename", action="store_true", help="do not rename the window (match by existing title)")
    s.add_argument("--timeout", type=float, default=120.0)
    s.set_defaults(fn=cmd_assign)

    s = sub.add_parser("snapshot", help="grab one frame of a display and write raw + cropped PNGs")
    s.add_argument("name")
    s.add_argument("--backend", choices=["auto", "wgc", "printwindow"])
    s.add_argument("--out", default="snapshots")
    s.add_argument("--wait", type=float, default=5.0)
    s.set_defaults(fn=cmd_snapshot)

    args = p.parse_args(argv)
    handlers: list[logging.Handler] = [logging.StreamHandler()]
    if args.cmd == "serve":
        from logging.handlers import RotatingFileHandler

        Path("logs").mkdir(exist_ok=True)
        handlers.append(RotatingFileHandler("logs/glasslink.log", maxBytes=2_000_000, backupCount=3, encoding="utf-8"))
    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
        datefmt="%H:%M:%S",
        handlers=handlers,
    )
    sys.exit(args.fn(args))
