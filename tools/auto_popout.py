#!/usr/bin/env python3
"""Pop out the displays and assign them (thin CLI over glasslink.popout), and calibrate aircraft profiles.

Pop-out uses a per-aircraft profile: after a camera reset and a fixed zoom (via SimConnect) the displays sit at
fixed fractions of the sim window, so it works with a dark (cold and dark) cockpit. When the displays are powered
the PFD detection refines the points. Profiles live in glasslink/popout.py (built-in) and config.json
(popout.profiles).

    python tools/auto_popout.py                          # pop out all missing sim displays (auto profile by aircraft title)
    python tools/auto_popout.py --only ecam_lower
    python tools/auto_popout.py --profile Fenix          # force a profile
    python tools/auto_popout.py --no-detect              # profile points only (what a cold-and-dark start uses)
    python tools/auto_popout.py --points ecam_lower=2000,1380   # one-off overrides in screen pixels

Calibrating a new aircraft (do this once per aircraft, displays may be dark):
    python tools/auto_popout.py --calibrate              # resets camera + zoom, writes calibrate.png with the
                                                         # current profile points drawn, prints them in pixels
    python tools/auto_popout.py --save-profile "PMDG 737" --zoom 30 --points pfd=1230,1180 nd=1500,1180 ...
                                                         # stores the points as fractions in config.json
"""

from __future__ import annotations

import argparse
import logging
import os
import sys
import time
from pathlib import Path

_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(_ROOT))

try:
    import win32api  # noqa: F401
except ModuleNotFoundError:
    _venv_py = _ROOT / ".venv" / "Scripts" / "python.exe"
    if _venv_py.exists() and Path(sys.executable).resolve() != _venv_py.resolve():
        os.execv(str(_venv_py), [str(_venv_py), str(Path(__file__).resolve()), *sys.argv[1:]])
    sys.exit(f"pywin32 not installed for {sys.executable}. Run: {_venv_py} {__file__}")

from glasslink.config import load_config, save_config  # noqa: E402
from glasslink.popout import (  # noqa: E402
    POPOUT_DEFAULTS,
    detect_points,
    ensure_popouts,
    normalise_points,
    profile_points,
    profiles,
    select_profile,
    sim_main_window,
    try_sim_camera,
)


def parse_points(items: list[str] | None) -> dict[str, tuple[int, int]]:
    out: dict[str, tuple[int, int]] = {}
    for item in items or []:
        name, xy = item.split("=")
        x, y = xy.split(",")
        out[name.strip()] = (int(x), int(y))
    return out


def calibrate(cfg: dict, profile_name: str | None, zoom: float | None, no_camera: bool) -> None:
    """Reset camera + zoom, grab the sim, draw the profile points, write calibrate.png."""
    import cv2

    from glasslink.capture.printwindow import grab_window

    sim = sim_main_window()
    if sim is None:
        sys.exit("sim window not found - is the sim running and in the cockpit?")
    title = None
    cam = None if no_camera else try_sim_camera()
    try:
        if cam is not None:
            title = cam.title
            if not cam.in_cockpit:
                sys.exit(f"not in cockpit view (camera state {cam.camera_state})")
        if profile_name:
            prof = profiles(cfg).get(profile_name)
            key = profile_name
        else:
            key, prof = select_profile(cfg, title)
        z = float(zoom if zoom is not None else (prof or {}).get("zoom", POPOUT_DEFAULTS["zoom"]))
        print(f"aircraft: '{title}'  profile: {key or 'none'}  zoom: {z:.0f}")
        if cam is not None:
            cam.reset()
            time.sleep(0.8)
            cam.zoom = z
            time.sleep(1.2)
        sim = sim_main_window() or sim
        frame = grab_window(sim.hwnd)
        if frame is None:
            sys.exit("could not grab the sim window")
        img = frame[..., :3].copy()
        pts = profile_points(prof, sim) if prof else {}
        det = detect_points(sim.hwnd) or {}
        for name, (x, y) in pts.items():
            cv2.drawMarker(img, (x - sim.client.left, y - sim.client.top), (0, 200, 255), cv2.MARKER_CROSS, 40, 3)
            cv2.putText(img, name, (x - sim.client.left + 22, y - sim.client.top - 10), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 200, 255), 2)
        for name, (x, y) in det.items():
            cv2.drawMarker(img, (x - sim.client.left, y - sim.client.top), (0, 255, 0), cv2.MARKER_TILTED_CROSS, 30, 2)
        cv2.imwrite("calibrate.png", img)
        print("wrote calibrate.png  (orange = profile points, green = detected displays, if any)")
        print("profile points (screen px):", pts)
        print("detected points (screen px):", det or "none (displays dark?)")
        print("sim window client area:", sim.client.as_list())
        print("If the orange crosses are not on the displays, re-run with:")
        print(f'  auto_popout.py --save-profile "{key or "NAME"}" --zoom {z:.0f} --points pfd=X,Y nd=X,Y ecam_upper=X,Y ecam_lower=X,Y')
        print("using pixel positions read off calibrate.png (add the client-area offset if the window is not at 0,0).")
    finally:
        if cam is not None:
            cam.reset()
            cam.close()


def save_profile(cfg: dict, config_path, name: str, zoom: float | None, points: dict[str, tuple[int, int]], detect: str | None) -> None:
    sim = sim_main_window()
    if sim is None:
        sys.exit("sim window not found (needed to convert pixels to window fractions)")
    if not points:
        sys.exit("--save-profile needs --points name=x,y ...")
    prof = cfg.setdefault("popout", {}).setdefault("profiles", {}).setdefault(name, {})
    prof["points"] = {**prof.get("points", {}), **normalise_points(points, sim)}
    if zoom is not None:
        prof["zoom"] = float(zoom)
    if detect is not None:
        prof["detect"] = detect or None
    path = save_config(cfg, config_path)
    print(f"profile '{name}' saved to {path}: {prof}")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("-c", "--config", type=Path, default=None)
    ap.add_argument("--only", help="comma separated display names")
    ap.add_argument("--profile", help="force this profile name instead of matching the aircraft title")
    ap.add_argument("--points", nargs="*", help="name=x,y (screen pixels): overrides, or the points for --save-profile")
    ap.add_argument("--no-camera", action="store_true", help="do not reset/zoom the camera via SimConnect")
    ap.add_argument("--no-detect", action="store_true", help="use profile points only, skip display detection")
    ap.add_argument("--calibrate", action="store_true", help="reset camera+zoom, write calibrate.png with the profile points drawn")
    ap.add_argument("--save-profile", metavar="NAME", help="store --points (and --zoom) as profile NAME in config.json")
    ap.add_argument("--zoom", type=float, help="camera zoom for --calibrate / --save-profile")
    ap.add_argument("--detect", choices=["pfd_sphere", ""], help="detection method to store with --save-profile ('' = none)")
    ap.add_argument("--grab", action="store_true", help="only write sim.png of the current view and print detected points")
    args = ap.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")

    cfg = load_config(args.config)
    points = parse_points(args.points)

    if args.grab:
        import cv2

        from glasslink.capture.printwindow import grab_window

        sim = sim_main_window()
        if sim is None:
            sys.exit("sim window not found")
        cv2.imwrite("sim.png", grab_window(sim.hwnd)[..., :3])
        print("wrote sim.png; detected:", detect_points(sim.hwnd))
        return
    if args.calibrate:
        calibrate(cfg, args.profile, args.zoom, args.no_camera)
        return
    if args.save_profile:
        save_profile(cfg, args.config, args.save_profile, args.zoom, points, args.detect)
        return

    names = [n.strip() for n in args.only.split(",")] if args.only else None
    result = ensure_popouts(cfg, names, points or None, use_camera=not args.no_camera, config_path=args.config,
                            say=print, use_detection=not args.no_detect, profile_name=args.profile)
    missing = [n for n, h in result.items() if h is None]
    print("done -", "all displays popped out" if not missing else f"still missing: {missing}")
    sys.exit(1 if missing else 0)


if __name__ == "__main__":
    main()
