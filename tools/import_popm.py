#!/usr/bin/env python3
"""Import MSFS Pop Out Panel Manager profiles into GlassLink pop-out profiles.

POPM stores, per panel, the screen pixel where you placed the coloured dot, the camera it was placed in
(Cockpit Pilot view or one of the aircraft's Instrument views) and the cockpit zoom. That is exactly what our
profiles need, converted to fractions of the sim window. Panel names are mapped to our display names by keyword.

    python tools/import_popm.py                       # list POPM profiles
    python tools/import_popm.py --import "Fenix"      # write it as popout.profiles["Fenix"] in config.json
    python tools/import_popm.py --import "FSLabs a321neo" --as "FSLabs" --screen 2560x1440
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(_ROOT))

from glasslink.config import load_config, save_config  # noqa: E402

POPM_FILE = Path(os.environ.get("APPDATA", "")) / "MSFS Pop Out Panel Manager 2024" / "userprofiledata.json"

NAME_MAP = [  # (keywords that must all appear, our display name)
    (("upper", "ecam"), "ecam_upper"), (("ecam", "upper"), "ecam_upper"), (("e/wd",), "ecam_upper"),
    (("lower", "ecam"), "ecam_lower"), (("ecam", "lower"), "ecam_lower"), (("sd",), "ecam_lower"),
    (("fo", "pfd"), "fo_pfd"), (("fo", "nd"), "fo_nd"),
    (("pfd",), "pfd"), (("nd",), "nd"), (("eicas",), "ecam_upper"), (("mfd",), "ecam_lower"),
]


def map_name(panel_name: str) -> str | None:
    n = panel_name.lower()
    for keys, ours in NAME_MAP:
        if all(k in n for k in keys):
            return ours
    return None


def convert(profile: dict, screen: tuple[int, int]) -> dict:
    sw, sh = screen
    zoom = float(profile.get("PanelSourceCockpitZoomFactor") or 50)
    points: dict[str, dict] = {}
    for panel in profile.get("PanelConfigs", []):
        ours = map_name(panel.get("PanelName", ""))
        src = panel.get("PanelSource") or {}
        if not ours or "X" not in src:
            continue
        cam = panel.get("FixedCameraConfig") or {}
        camera = {"type": int(cam.get("CameraType", 1)), "index": int(cam.get("CameraIndex", 1)), "name": cam.get("Name", "")}
        points[ours] = {"xy": [round(src["X"] / sw, 4), round(src["Y"] / sh, 4)], "camera": camera,
                        "popm_panel": panel.get("PanelName")}
    return {"zoom": zoom, "detect": None, "camera": {"type": 1, "index": 1, "name": "Cockpit Pilot"}, "points": points,
            "source": f"POPM profile '{profile.get('Name')}' bindings {profile.get('AircraftBindings')} at {sw}x{sh}"}


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("-c", "--config", type=Path, default=None)
    ap.add_argument("--file", type=Path, default=POPM_FILE, help="POPM userprofiledata.json")
    ap.add_argument("--import", dest="imp", metavar="NAME", help="POPM profile name to import")
    ap.add_argument("--as", dest="alias", metavar="KEY", help="profile key in config (substring of the sim aircraft title); default: POPM name")
    ap.add_argument("--screen", default=None, help="screen size the POPM dots were placed on, e.g. 2560x1440 (default: current primary screen)")
    args = ap.parse_args()

    data = json.load(open(args.file, encoding="utf-8-sig"))
    profiles = {p["Name"]: p for p in data.get("Profiles", [])}
    if not args.imp:
        for name, p in profiles.items():
            panels = ", ".join(f"{c['PanelName']}->{map_name(c['PanelName']) or '?'}" for c in p.get("PanelConfigs", []))
            print(f"{name!r}: zoom {p.get('PanelSourceCockpitZoomFactor')}, bindings {p.get('AircraftBindings')}: {panels}")
        print("\nimport with: import_popm.py --import \"<name>\" [--as <aircraft title substring>]")
        return
    if args.imp not in profiles:
        sys.exit(f"no POPM profile named {args.imp!r}; available: {list(profiles)}")
    if args.screen:
        screen = tuple(int(v) for v in args.screen.lower().split("x"))
    else:
        import ctypes

        u = ctypes.windll.user32
        u.SetProcessDPIAware()
        screen = (u.GetSystemMetrics(0), u.GetSystemMetrics(1))
    prof = convert(profiles[args.imp], screen)
    key = args.alias or args.imp
    cfg = load_config(args.config)
    cfg.setdefault("popout", {}).setdefault("profiles", {})[key] = prof
    path = save_config(cfg, args.config)
    print(f"imported POPM profile {args.imp!r} as popout.profiles[{key!r}] into {path}:")
    print(json.dumps(prof, indent=2))
    print("\nNote: the key must be a substring of the aircraft title the sim reports (e.g. 'Fenix', 'FSLabs'); "
          "use --as to set it. Run tools/auto_popout.py --calibrate --profile <key> to check the points.")


if __name__ == "__main__":
    main()
