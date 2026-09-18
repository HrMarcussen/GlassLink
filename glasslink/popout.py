"""Create MSFS pop-out windows programmatically and assign them to displays.

MSFS forgets pop-outs between sessions and the only way to create one is Right-Alt + click on the
instrument in the virtual cockpit. To make that independent of the user's camera and resolution:

1. Through SimConnect the cockpit camera is reset (CAMERA REQUEST ACTION = 1) and zoomed out to a
   fixed value (COCKPIT CAMERA ZOOM), so every display is fully in view at a known place.
2. The PFD is located in a picture of the sim window by its blue attitude sphere; the other displays
   are derived from it with fixed offsets (scaled with the sphere size, i.e. with zoom/resolution).
3. Right-Alt + click is sent for each missing display, the new window is renamed `GlassLink:<name>`,
   resized/parked from config.json, and the previous camera zoom is restored.

Camera pitch/yaw cannot be written on MSFS 2024, hence the reset + zoom approach.
"""

from __future__ import annotations

import logging
import os
import time
from typing import Any, Callable

import win32api
import win32con
import win32gui

from . import windows as win
from .config import save_config

log = logging.getLogger(__name__)

SIM_PROCESS = "FlightSimulator2024.exe"
SIM_CLASS = "AceApp"
SIM_TITLE = "Microsoft Flight Simulator"

# Offsets of the other displays from the PFD attitude-sphere centre, in units of the sky-blob width
# (Fenix A320, captain side). Independent of camera pitch, scales with zoom and resolution.
RELATIVE_OFFSETS = {"nd": (2.71, -0.1), "ecam_upper": (7.4, -0.4), "ecam_lower": (7.4, 2.1)}
CAMERA_STATE_COCKPIT = 2

# Per-aircraft profiles. Keys are substrings of the sim's aircraft TITLE. `points` are fractions of the sim
# window's client area (x/width, y/height) as seen after the camera reset at `zoom`; they work with a dark
# (cold and dark) cockpit and at any resolution with the same aspect ratio. `detect` names an optional check
# that refines the points when the displays are powered ("pfd_sphere" = blue attitude sphere + RELATIVE_OFFSETS).
# Measured on the Fenix A320 at 2560x1440, camera reset, zoom 30. Override or add profiles in config.json under
# popout.profiles, or create them with `tools/auto_popout.py --calibrate` / `--save-profile`.
DEFAULT_PROFILES: dict[str, dict[str, Any]] = {
    "Fenix": {
        "zoom": 30,
        "detect": "pfd_sphere",
        "points": {
            "pfd": [0.4832, 0.8160],
            "nd": [0.5805, 0.8090],
            "ecam_upper": [0.7488, 0.7903],
            "ecam_lower": [0.7488, 0.9500],
            # FO side: out of reach from the left seat, clicked from the sim's copilot seat view (measured 18 Sept 2026)
            "fo_nd": {"xy": [0.4063, 0.8167], "camera": {"mode": "view", "type": 1, "index": 4}},
            "fo_pfd": {"xy": [0.5039, 0.8167], "camera": {"mode": "view", "type": 1, "index": 4}},
        },
        # Effective DU brightness 0..1 as rendered by the Fenix (knob position is A_DISPLAY_BRIGHTNESS_*).
        # The pop-out windows do not dim with the knobs, so the server applies it to the modules.
        "brightness": {
            "pfd": "N_DISPLAY_BRIGHTNESS_CO",
            "nd": "N_DISPLAY_BRIGHTNESS_CI",
            "ecam_upper": "N_DISPLAY_BRIGHTNESS_ECAM_U",
            "ecam_lower": "N_DISPLAY_BRIGHTNESS_ECAM_L",
            "fo_pfd": "N_DISPLAY_BRIGHTNESS_FO",
            "fo_nd": "N_DISPLAY_BRIGHTNESS_FI",
        },
        # Fenix EFB setting "Home Cockpit Mode" makes the pop-outs dim with the knobs themselves. It is persisted
        # in this file; while it is on, the DMC must not dim a second time.
        "popout_dimming": {
            "file": r"C:\ProgramData\Fenix\FenixSim A320\persistancy.xml",
            "xml_tag": "homeCockpitMode",
            "on_value": "true",
            "name": "Fenix Home Cockpit Mode",
        },
    },
}


_pd_cache: dict[str, tuple[float, float, bool]] = {}     # file -> (checked at, mtime, result)


def popout_dims_itself(prof: dict[str, Any] | None) -> str | None:
    """Name of the aircraft feature that already dims the pop-out picture, if it is switched on; else None.
    Reads the aircraft's settings file at most every 5 s and only re-parses it when it changed."""
    import re
    import time as _time

    spec = (prof or {}).get("popout_dimming") or {}
    path, tag = spec.get("file"), spec.get("xml_tag")
    if not path or not tag:
        return None
    now = _time.time()
    checked, mtime, result = _pd_cache.get(path, (0.0, -1.0, False))
    if now - checked > 5:
        try:
            m = os.path.getmtime(path)
            if m != mtime:
                text = open(path, encoding="utf-8", errors="replace").read()
                found = re.search(r"<%s>\s*([^<]*?)\s*</%s>" % (re.escape(tag), re.escape(tag)), text)
                result = bool(found) and found.group(1).strip().lower() == str(spec.get("on_value", "true")).lower()
                mtime = m
        except OSError:
            result, mtime = False, -1.0
        _pd_cache[path] = (now, mtime, result)
    return str(spec.get("name") or "aircraft setting") if result else None

POPOUT_DEFAULTS: dict[str, Any] = {
    "auto": False,          # server pops missing displays out by itself (needs SimConnect)
    "aircraft": "Fenix",    # substring of the sim's aircraft TITLE that enables auto pop-out
    "zoom": 30,             # COCKPIT CAMERA ZOOM used while popping out (30 = wide, all DUs visible)
    "grace_s": 10,          # seconds a display may be missing before auto pop-out kicks in
    "retry_s": 60,          # minimum seconds between auto attempts
    "max_attempts": 2,      # then the DMC stops moving the camera for that display until it is learned again
    # Camera to return to after popping out: "current" = save the view you had into custom-camera slot
    # `camera_slot` (Ctrl+Alt+N) before the reset and load it again (Alt+N) afterwards; an integer 0-9 =
    # load that custom camera slot afterwards; null/"" = just reset and restore the zoom.
    "camera_restore": "current",
    "camera_slot": 9,       # unused since 0.4 (kept so old configs load)
    # Key combination that loads the user's own flying view (a sim custom camera) after a pop-out or Learn, exactly as
    # bound in the sim's controls, e.g. "shift+f1". None = stay in the pilot seat view.
    "camera_restore_key": None,
}


# ---------------------------------------------------------------------------------------------
# SimConnect camera control (optional: everything degrades to "no camera control" without it)
# ---------------------------------------------------------------------------------------------
class SimCamera:
    def __init__(self) -> None:
        from SimConnect import Request, SimConnect  # imported lazily: only needed on the sim PC

        self.sm = SimConnect()
        self._title = Request((b"TITLE", b"String"), self.sm, _time=0)
        self._state = Request((b"CAMERA STATE", b"Enum"), self.sm, _time=0, _settable=True)
        self._zoom = Request((b"COCKPIT CAMERA ZOOM", b"Percent"), self.sm, _time=0, _settable=True)
        self._action = Request((b"CAMERA REQUEST ACTION", b"Enum"), self.sm, _time=0, _settable=True)
        self._view_type = Request((b"CAMERA VIEW TYPE AND INDEX:index", b"Enum"), self.sm, _time=0, _settable=True)
        self._view_type.setIndex("0")
        self._view_index = Request((b"CAMERA VIEW TYPE AND INDEX:index", b"Enum"), self.sm, _time=0, _settable=True)
        self._view_index.setIndex("1")

    @property
    def view(self) -> tuple[int | None, int | None]:
        """(type, index) of the current camera view: type 1 = cockpit/pilot views, 2 = instrument views."""
        t, i = self._view_type.value, self._view_index.value
        return (None if t is None else int(t), None if i is None else int(i))

    def set_view(self, view_type: int, index: int) -> None:
        """Select a sim camera preset (same mechanism MSFS Pop Out Panel Manager uses): e.g. (1, 1) = Cockpit Pilot,
        (2, n) = the aircraft's Instrument view n+1."""
        self._view_type.value = int(view_type)
        self._view_index.value = int(index)

    @property
    def title(self) -> str:
        v = self._title.value
        return v.decode("utf-8", "ignore") if isinstance(v, bytes) else str(v or "")

    @property
    def camera_state(self) -> int | None:
        v = self._state.value
        return None if v is None else int(v)

    @property
    def in_cockpit(self) -> bool:
        return self.camera_state == CAMERA_STATE_COCKPIT

    @property
    def zoom(self) -> float | None:
        v = self._zoom.value
        return None if v is None else float(v)

    @zoom.setter
    def zoom(self, value: float) -> None:
        self._zoom.value = float(value)

    def reset(self) -> None:
        self._action.value = 1

    def close(self) -> None:
        try:
            self.sm.exit()
        except Exception:  # noqa: BLE001
            pass


def try_sim_camera() -> SimCamera | None:
    try:
        return SimCamera()
    except Exception as exc:  # noqa: BLE001
        log.info("SimConnect not available (%s); continuing without camera control", exc)
        return None


# ---------------------------------------------------------------------------------------------
# Display detection in a picture of the sim window
# ---------------------------------------------------------------------------------------------
def detect_pfd(frame) -> tuple[int, int, int] | None:
    """Find the PFD attitude sphere (largest dark saturated-blue blob) in a BGR(A) frame.
    Returns (centre_x, horizon_y, blob_width) or None."""
    import cv2
    import numpy as np

    b = frame[..., 0].astype(int)
    g = frame[..., 1].astype(int)
    r = frame[..., 2].astype(int)
    mask = ((b > 90) & (b > 1.4 * g) & (g > 1.6 * r) & (r < 70)).astype(np.uint8)
    n, _labels, stats, _cents = cv2.connectedComponentsWithStats(mask, connectivity=8)
    best = None
    for i in range(1, n):
        x, y, w, h, area = stats[i]
        if area < 300 or w < 30 or h < 12:
            continue
        aspect = w / max(h, 1)
        if not 1.3 <= aspect <= 4.0:  # the visible sky half of the sphere is wider than tall
            continue
        if best is None or area > best[4]:
            best = (x, y, w, h, area)
    if best is None:
        return None
    x, y, w, h, _ = best
    return int(x + w // 2), int(y + h), int(w)


def detect_points(sim_hwnd: int) -> dict[str, tuple[int, int]] | None:
    from .capture.printwindow import grab_window

    frame = grab_window(sim_hwnd)
    if frame is None:
        return None
    hit = detect_pfd(frame)
    if hit is None:
        return None
    cx, cy, w = hit
    pts = {"pfd": (cx, cy)}
    for name, (ox, oy) in RELATIVE_OFFSETS.items():
        pts[name] = (int(cx + ox * w), int(cy + oy * w))
    return pts


# ---------------------------------------------------------------------------------------------
# Input
# ---------------------------------------------------------------------------------------------
def _move_abs(x: int, y: int) -> None:
    sw, sh = win32api.GetSystemMetrics(0), win32api.GetSystemMetrics(1)
    ax, ay = int(x * 65535 / (sw - 1)), int(y * 65535 / (sh - 1))
    win32api.mouse_event(win32con.MOUSEEVENTF_MOVE | win32con.MOUSEEVENTF_ABSOLUTE, ax, ay, 0, 0)


def bring_to_front(hwnd: int) -> bool:
    for _ in range(5):
        try:
            win32gui.SetForegroundWindow(hwnd)
        except Exception:  # noqa: BLE001
            # An Alt tap lifts the foreground lock so the next attempt succeeds.
            win32api.keybd_event(win32con.VK_MENU, 0, 0, 0)
            win32api.keybd_event(win32con.VK_MENU, 0, win32con.KEYEVENTF_KEYUP, 0)
        time.sleep(0.4)
        if win32gui.GetForegroundWindow() == hwnd:
            return True
    return False


def send_key(sim_hwnd: int, vk: int, ctrl: bool = False, alt: bool = False) -> bool:
    """Press a key (with optional Ctrl/Alt) in the sim window using real input events."""
    if not bring_to_front(sim_hwnd):
        return False
    mods = ([win32con.VK_CONTROL] if ctrl else []) + ([win32con.VK_MENU] if alt else [])
    for m in mods:
        win32api.keybd_event(m, win32api.MapVirtualKey(m, 0), 0, 0)
        time.sleep(0.05)
    win32api.keybd_event(vk, win32api.MapVirtualKey(vk, 0), 0, 0)
    time.sleep(0.1)
    win32api.keybd_event(vk, win32api.MapVirtualKey(vk, 0), win32con.KEYEVENTF_KEYUP, 0)
    for m in reversed(mods):
        time.sleep(0.05)
        win32api.keybd_event(m, win32api.MapVirtualKey(m, 0), win32con.KEYEVENTF_KEYUP, 0)
    return True


_MOD_VK = {"shift": 0x10, "ctrl": 0x11, "control": 0x11, "alt": 0x12}


def parse_combo(text: str) -> list[int]:
    """'shift+f1' -> virtual key codes, modifiers first. Keys: a-z, 0-9, f1-f24. Raises ValueError otherwise."""
    parts = [p.strip().lower() for p in str(text).split("+") if p.strip()]
    if not parts:
        raise ValueError("empty key combination")
    mods, keys = [], []
    for p in parts:
        if p in _MOD_VK:
            mods.append(_MOD_VK[p])
        elif len(p) == 1 and (p.isdigit() or "a" <= p <= "z"):
            keys.append(ord(p.upper()))
        elif p[0] == "f" and p[1:].isdigit() and 1 <= int(p[1:]) <= 24:
            keys.append(0x70 + int(p[1:]) - 1)
        else:
            raise ValueError(f"unknown key '{p}' (use shift, ctrl, alt, a-z, 0-9, f1-f24)")
    if len(keys) != 1:
        raise ValueError("a key combination needs exactly one key besides shift/ctrl/alt")
    return mods + keys


def send_combo(sim_hwnd: int, combo: str) -> bool:
    """Press a key combination such as 'shift+f1' in the sim window using real input events."""
    vks = parse_combo(combo)
    if not bring_to_front(sim_hwnd):
        return False
    for vk in vks:
        win32api.keybd_event(vk, win32api.MapVirtualKey(vk, 0), 0, 0)
        time.sleep(0.06)
    time.sleep(0.1)
    for vk in reversed(vks):
        win32api.keybd_event(vk, win32api.MapVirtualKey(vk, 0), win32con.KEYEVENTF_KEYUP, 0)
        time.sleep(0.06)
    return True


def custom_camera(sim_hwnd: int, slot: int, save: bool) -> bool:
    """MSFS default bindings: Ctrl+Alt+<digit> saves the current cockpit camera as custom camera <digit>,
    Alt+<digit> loads it."""
    vk = ord(str(int(slot) % 10))
    return send_key(sim_hwnd, vk, ctrl=save, alt=True)


def ralt_click(sim_hwnd: int, x: int, y: int, hold: float = 0.3) -> bool:
    """Right-Alt + left click at screen point (x, y). Real input events (the sim ignores SetCursorPos)."""
    if not bring_to_front(sim_hwnd):
        log.warning("could not bring the sim window to the front; not clicking")
        return False
    for i in range(8):  # small sweep so the sim shows its cursor
        _move_abs(x - 8 + i, y)
        time.sleep(0.05)
    time.sleep(0.4)
    sc = win32api.MapVirtualKey(win32con.VK_RMENU, 0)
    win32api.keybd_event(win32con.VK_RMENU, sc, win32con.KEYEVENTF_EXTENDEDKEY, 0)
    time.sleep(hold)
    win32api.mouse_event(win32con.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.12)
    win32api.mouse_event(win32con.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
    time.sleep(0.2)
    win32api.keybd_event(win32con.VK_RMENU, sc, win32con.KEYEVENTF_EXTENDEDKEY | win32con.KEYEVENTF_KEYUP, 0)
    return True


# ---------------------------------------------------------------------------------------------
# Main procedure
# ---------------------------------------------------------------------------------------------
def sim_main_window() -> win.WindowInfo | None:
    return win.match_window({"process": SIM_PROCESS, "class": SIM_CLASS, "title": SIM_TITLE})


def stray_popouts() -> list[win.WindowInfo]:
    """Pop-out windows of the sim that are not GlassLink's: popped out by hand or by another tool. A display that is
    already popped out this way cannot be popped out again, so a click on it opens nothing."""
    main = sim_main_window()
    return [w for w in win.enum_windows(process=SIM_PROCESS)
            if w.cls == SIM_CLASS and w.visible and (main is None or w.hwnd != main.hwnd)
            and not w.title.startswith("GlassLink:") and w.title != SIM_TITLE]


def popout_exists(name: str) -> win.WindowInfo | None:
    return win.match_window({"process": SIM_PROCESS, "title": f"GlassLink:{name}"})


def profiles(cfg: dict[str, Any]) -> dict[str, dict[str, Any]]:
    """Built-in profiles merged with popout.profiles from the config (config wins, points merge per display)."""
    out = {k: {**v, "points": dict(v.get("points", {}))} for k, v in DEFAULT_PROFILES.items()}
    for k, v in (cfg.get("popout", {}).get("profiles") or {}).items():
        merged = dict(out.get(k, {}))
        merged.update({kk: vv for kk, vv in v.items() if kk != "points"})
        merged["points"] = {**out.get(k, {}).get("points", {}), **(v.get("points") or {})}
        out[k] = merged
    return out


def select_profile(cfg: dict[str, Any], title: str | None) -> tuple[str | None, dict[str, Any] | None]:
    """Profile whose key is a substring of the aircraft title (case-insensitive)."""
    if not title:
        return None, None
    for key, prof in profiles(cfg).items():
        if key.lower() in title.lower():
            return key, prof
    return None, None


# Seat views every aircraft has (SimConnect "CAMERA VIEW TYPE AND INDEX", type 1 = pilot views). Selected directly,
# without key presses. Measured with the Fenix: 1 = pilot, 3 = wide view of the whole panel, 4 = copilot.
SEAT_VIEWS: dict[str, dict[str, Any] | None] = {
    "standard": None,                                       # the profile's own camera (reset of the pilot view)
    "copilot": {"mode": "view", "type": 1, "index": 4},
}
PILOT_VIEW = (1, 1)


def point_spec(prof: dict[str, Any], name: str) -> dict[str, Any] | None:
    """Normalised point entry: {"xy": [fx, fy], "camera": {...}}. Accepts the short form [fx, fy] (profile camera)."""
    raw = prof.get("points", {}).get(name)
    if raw is None:
        return None
    if isinstance(raw, dict) and (raw.get("camera") or {}).get("mode") == "custom":
        return None      # 0.4 development builds stored sim custom cameras; the sim ignores those keys: learn again
    if isinstance(raw, dict):
        return {"xy": list(raw["xy"]), "camera": raw.get("camera") or prof.get("camera") or {"mode": "reset"}}
    return {"xy": list(raw), "camera": prof.get("camera") or {"mode": "reset"}}


def camera_key(cam: dict[str, Any]) -> str:
    if cam.get("mode", "reset") == "reset" and "type" not in cam:
        return "reset"
    return f"view:{cam.get('type')}:{cam.get('index')}"


def profile_points(prof: dict[str, Any], sim: win.WindowInfo, names: list[str] | None = None) -> dict[str, tuple[int, int]]:
    """Fixed click points of a profile scaled to the sim window's client area (screen pixels)."""
    c = sim.client
    out: dict[str, tuple[int, int]] = {}
    for name in names or list(prof.get("points", {})):
        spec = point_spec(prof, name)
        if spec:
            fx, fy = spec["xy"]
            out[name] = (int(round(c.left + fx * c.width)), int(round(c.top + fy * c.height)))
    return out


def frame_motion(a, b) -> float:
    """Mean absolute difference (0..255) between two grabs of the sim, on a coarse grey grid. A camera that is
    still moving changes every cell (tens); lights, traffic and animated displays change a few cells (< 2)."""
    import numpy as np

    def coarse(f):
        g = f[..., :3].astype(np.float32).mean(axis=2)
        h, w = g.shape
        ys = np.linspace(0, h - 1, 36).astype(int)
        xs = np.linspace(0, w - 1, 64).astype(int)
        return g[np.ix_(ys, xs)]

    ca, cb = coarse(a), coarse(b)
    if ca.shape != cb.shape:
        return 255.0
    return float(np.abs(ca - cb).mean())


def wait_until_still(sim_hwnd: int, say: Callable[[str], None], max_s: float = 12.0, grab=None,
                     interval_s: float = 0.4, threshold: float = 2.0) -> bool:
    """Block until the sim's picture has stopped moving (camera transitions are animated and can take several
    seconds, e.g. right after loading). Two quiet comparisons in a row count as still. Returns False on timeout."""
    if grab is None:
        from .capture.printwindow import grab_window as grab
    t0 = time.monotonic()
    prev = grab(sim_hwnd)
    quiet = 0
    while time.monotonic() - t0 < max_s:
        time.sleep(interval_s)
        cur = grab(sim_hwnd)
        if prev is None or cur is None:
            prev = cur
            continue
        motion = frame_motion(prev, cur)
        quiet = quiet + 1 if motion < threshold else 0
        prev = cur
        if quiet >= 2:
            waited = time.monotonic() - t0
            if waited > 1.5:
                say(f"camera settled after {waited:.1f} s")
            return True
    say(f"camera still moving after {max_s:.0f} s; continuing anyway")
    return False


def apply_camera(cam_ctl: "SimCamera | None", cam: dict[str, Any], zoom: float, say: Callable[[str], None],
                 sim_hwnd: int | None = None) -> None:
    """Put the sim camera into the state a point set was calibrated for, and wait until it has arrived."""
    if cam_ctl is None:
        return
    if camera_key(cam) == "reset":
        say(f"camera: reset + zoom {zoom:.0f}")
        if cam_ctl.view != PILOT_VIEW and cam_ctl.view[0] is not None:
            cam_ctl.set_view(*PILOT_VIEW)
            time.sleep(1.0)
        cam_ctl.reset()
        time.sleep(0.8)
    else:
        say(f"camera: view type {cam['type']} index {cam['index']} + zoom {zoom:.0f}")
        cam_ctl.set_view(int(cam["type"]), int(cam["index"]))
        time.sleep(1.0)
        cam_ctl.reset()                       # the user may have looked around in that view earlier
        time.sleep(0.8)
    cam_ctl.zoom = zoom
    time.sleep(1.2)
    if sim_hwnd:
        wait_until_still(sim_hwnd, say)


def restore_camera(cam: "SimCamera", old_view, old_zoom, pcfg: dict[str, Any], sim_hwnd: int,
                   say: Callable[[str], None]) -> None:
    """Back to the seat view and zoom the user had. Done over SimConnect. (Until 0.3 this tried to save and reload the
    exact view as a sim custom camera with Ctrl+Alt+9 / Alt+9; measured 18 Sept 2026: the sim ignores those injected
    keys.) `camera_restore` set to a number still loads that custom camera slot afterwards, for those it works for."""
    if old_view and old_view[0] is not None and cam.view != tuple(old_view):
        cam.set_view(int(old_view[0]), int(old_view[1]))
        time.sleep(1.0)
    cam.reset()
    if old_zoom is not None:
        time.sleep(0.5)
        cam.zoom = old_zoom
    combo = pcfg.get("camera_restore_key")
    slot = pcfg.get("camera_restore")
    if not combo and (isinstance(slot, int) or (isinstance(slot, str) and slot.isdigit())):
        combo = f"alt+{int(slot) % 10}"                  # the MSFS 2020 default binding
    if combo:
        time.sleep(0.8)
        say(f"camera: back to your view with {combo}")
        try:
            send_combo(sim_hwnd, str(combo))
        except ValueError as exc:
            say(f"camera_restore_key: {exc}")


def normalise_points(points: dict[str, tuple[int, int]], sim: win.WindowInfo) -> dict[str, list[float]]:
    """Screen pixels -> fractions of the sim window's client area (for saving into a profile)."""
    c = sim.client
    return {n: [round((x - c.left) / c.width, 4), round((y - c.top) / c.height, 4)] for n, (x, y) in points.items()}


def ensure_popouts(
    cfg: dict[str, Any],
    names: list[str] | None = None,
    points_override: dict[str, tuple[int, int]] | None = None,
    use_camera: bool = True,
    config_path=None,
    say: Callable[[str], None] = log.info,
    aircraft_title: str | None = None,
    use_detection: bool = True,
    profile_name: str | None = None,
) -> dict[str, int | None]:
    """Pop out and assign every display in `names` (default: all configured sim displays) that has no window.
    Click points come from the aircraft profile (fixed fractions of the sim window after the camera reset), refined
    by display detection when the displays are powered. Returns {name: hwnd or None}."""
    pcfg = {**POPOUT_DEFAULTS, **cfg.get("popout", {})}
    displays = cfg["displays"]
    names = names or [n for n, d in displays.items() if d.get("match", {}).get("process", SIM_PROCESS) == SIM_PROCESS]
    result: dict[str, int | None] = {}
    missing = []
    for n in names:
        w = popout_exists(n)
        result[n] = w.hwnd if w else None
        if w is None:
            missing.append(n)
    if not missing:
        say("all pop-outs already exist")
        return result

    sim = sim_main_window()
    if sim is None:
        say(f"{SIM_PROCESS} main window not found - is the sim running?")
        return result

    cam = try_sim_camera() if use_camera else None
    old_zoom = None
    old_view = None
    try:
        if cam is not None:
            if not cam.in_cockpit:
                say(f"sim is not in the cockpit view (camera state {cam.camera_state}); not popping out")
                return result
            aircraft_title = aircraft_title or cam.title
        all_profiles = profiles(cfg)
        if profile_name:
            prof_key, prof = profile_name, all_profiles.get(profile_name)
        else:
            prof_key, prof = select_profile(cfg, aircraft_title)
            if prof is None and len(all_profiles) == 1 and not aircraft_title:
                prof_key, prof = next(iter(all_profiles.items()))
        if prof is None:
            say(f"no pop-out profile for aircraft '{aircraft_title}' (profiles: {list(all_profiles)}); "
                f"create one with tools/auto_popout.py --calibrate")
            return result
        say(f"using profile '{prof_key}' for aircraft '{aircraft_title}'")
        zoom = float(prof.get("zoom", pcfg["zoom"]))
        if cam is not None:
            old_zoom = cam.zoom
            old_view = cam.view
            say(f"camera zoom was {old_zoom:.0f}" if old_zoom is not None else "camera zoom unknown")

        # Group the missing displays by the camera view their points were calibrated in (a profile imported from
        # Pop Out Panel Manager may use the pilot view for some displays and an instrument view for others).
        groups: dict[str, tuple[dict[str, Any], list[str]]] = {}
        for name in missing:
            spec = point_spec(prof, name)
            if spec is None and name not in (points_override or {}):
                say(f"{name}: not in profile '{prof_key}', skipped")
                continue
            camspec = spec["camera"] if spec else (prof.get("camera") or {"mode": "reset"})
            groups.setdefault(camera_key(camspec), (camspec, []))[1].append(name)

        sw, sh = win32api.GetSystemMetrics(0), win32api.GetSystemMetrics(1)
        for ckey, (camspec, group_names) in groups.items():
            apply_camera(cam, camspec, zoom, say, sim.hwnd)
            sim = sim_main_window() or sim
            points = profile_points(prof, sim, group_names)
            say(f"profile points ({ckey}): {points}")
            if use_detection and ckey == "reset" and prof.get("detect") == "pfd_sphere":
                agreed = None
                for attempt in range(4):
                    detected = detect_points(sim.hwnd) or {}
                    if not detected or "pfd" not in points:
                        break
                    dx = abs(detected["pfd"][0] - points["pfd"][0])
                    dy = abs(detected["pfd"][1] - points["pfd"][1])
                    agreed = dx < sim.client.width * 0.06 and dy < sim.client.height * 0.08
                    if agreed:
                        say(f"displays detected, refining points: {detected}")
                        points.update({k: v for k, v in detected.items() if k in group_names})
                        break
                    say(f"the PFD is visible at {detected['pfd']} but the profile expects {points['pfd']}: "
                        f"the view is not the calibrated one yet, waiting ({attempt + 1}/4)")
                    time.sleep(1.5)
                if agreed is False:
                    # Clicking now would pop out the wrong instruments (seen 18 Sept 2026: PFD -> ND, ND -> ISIS).
                    say("the view still does not match the profile; not clicking. Retrying later.")
                    continue
                if agreed is None:
                    say("displays not detected (dark cockpit?); using profile points")
            if points_override:
                points.update({k: v for k, v in points_override.items() if k in group_names})
            _click_group(cfg, sim, group_names, points, sw, sh, result, say)
        save_config(cfg, config_path)
    finally:
        if cam is not None:
            try:
                restore_camera(cam, old_view, old_zoom, pcfg, sim.hwnd, say)
            finally:
                cam.close()
    return result


def _click_group(cfg, sim, names, points, sw, sh, result, say) -> None:
    """Right-Alt + click each display in `names` at `points`, rename/resize/park the new windows."""
    displays = cfg["displays"]
    for name in names:
            if name not in points:
                say(f"{name}: no click point available, skipped")
                continue
            x, y = points[name]
            if not (20 <= x < sw - 20 and 20 <= y < sh - 40):
                say(f"{name}: click point ({x}, {y}) is off/at the screen edge, skipped")
                continue
            before = win.hwnd_set(SIM_PROCESS, SIM_CLASS)
            say(f"{name}: Right-Alt + click at ({x}, {y})")
            if not ralt_click(sim.hwnd, x, y):
                break
            new: set[int] = set()
            t0 = time.time()
            while time.time() - t0 < 15 and not new:
                time.sleep(0.5)
                new = win.hwnd_set(SIM_PROCESS, SIM_CLASS) - before
            if len(new) != 1:
                say(f"{name}: expected one new window, got {[hex(h) for h in new]}")
                continue
            time.sleep(1.0)
            hwnd = new.pop()
            dcfg = displays.setdefault(name, {})
            win.rename_window(hwnd, f"GlassLink:{name}")
            dcfg["match"] = {"process": SIM_PROCESS, "class": SIM_CLASS, "title": f"GlassLink:{name}"}
            size, pos = dcfg.get("client_size"), dcfg.get("position")
            if size:
                px, py = (pos or (None, None))
                win.set_client_size(hwnd, int(size[0]), int(size[1]), px, py)
            elif pos:
                win.move_window(hwnd, int(pos[0]), int(pos[1]))
            info = win.window_info(hwnd)
            say(f"{name}: {hex(hwnd)} client {info.client.width}x{info.client.height} at {info.rect.left},{info.rect.top}")
            result[name] = hwnd
            time.sleep(1.0)


class AutoPopout:
    """Background helper for the server: pops missing displays out when the sim is in the cockpit
    with a matching aircraft."""

    def __init__(self, cfg: dict[str, Any], hub, config_path=None) -> None:
        import threading

        self.cfg = cfg
        self.pcfg = {**POPOUT_DEFAULTS, **cfg.get("popout", {})}
        self.hub = hub
        self.config_path = config_path
        self._stop = threading.Event()
        self._missing_since: float | None = None
        self._last_attempt = -1e9
        self._next_probe = 0.0
        self._fails: dict[str, int] = {}         # display -> consecutive attempts that opened no window
        self._fail_spec: dict[str, Any] = {}     # the click point those attempts used: a new point is a new chance
        self.paused = False              # set while the display editor is learning a click point
        self.state: dict[str, Any] = {"status": "starting", "detail": "", "missing": [], "last_attempt": None}
        self._thread = threading.Thread(target=self._run, name="auto-popout", daemon=True)

    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()

    def retry(self, name: str | None = None) -> None:
        """Forget earlier failures (one display, or all) and try again soon."""
        for n in ([name] if name else list(self._fails)):
            self._fails.pop(n, None)
            self._fail_spec.pop(n, None)
        self._last_attempt = -1e9
        self._next_probe = 0.0

    def _missing(self) -> list[str]:
        out = []
        for name, d in self.cfg["displays"].items():
            if d.get("match", {}).get("process", SIM_PROCESS) != SIM_PROCESS:
                continue
            st = self.hub.get(name)
            if st is not None and st.window_hwnd is None:
                out.append(name)
        return out

    def _run(self) -> None:
        while not self._stop.wait(5.0):
            try:
                self._tick()
            except Exception as exc:  # noqa: BLE001 - never let the trigger thread die
                self.state.update(status="error", detail=f"{type(exc).__name__}: {exc}")
                log.exception("auto-popout tick failed")

    def _tick(self) -> None:
        if self.paused:
            self.state.update(status="waiting", detail="paused while a pop-out click point is being learned")
            return
        missing = self._missing()
        for n in [n for n in self._fails if n not in missing]:
            self.retry(n)                          # it has a window again
        if not missing:
            self._missing_since = None
            self.state.update(status="idle", detail="all displays have windows", missing=[])
            return
        if sim_main_window() is None:
            self._missing_since = None
            self.state.update(status="waiting", detail="sim not running", missing=missing)
            return
        now = time.monotonic()
        if self._missing_since is None:
            self._missing_since = now
        if now - self._missing_since < float(self.pcfg["grace_s"]):
            self.state.update(status="waiting", detail=f"grace period ({self.pcfg['grace_s']} s)", missing=missing)
            return
        if now - self._last_attempt < float(self.pcfg["retry_s"]):
            self.state.update(status="waiting", detail=f"retry in {int(self.pcfg['retry_s'] - (now - self._last_attempt))} s", missing=missing)
            return
        if now < self._next_probe:
            return                                 # nothing to do last time we looked: do not reconnect every 5 s
        self._next_probe = now + 20.0
        cam = try_sim_camera()
        if cam is None:
            self.state.update(status="waiting", detail="SimConnect not available yet", missing=missing)
            return
        try:
            title, in_cockpit = cam.title, cam.in_cockpit
        finally:
            cam.close()
        if not in_cockpit:
            self.state.update(status="waiting", detail=f"not in cockpit view (aircraft '{title}')", missing=missing)
            return
        prof_key, _prof = select_profile(self.cfg, title)
        if _prof is not None:
            # a display without a click point in this profile cannot be popped out: do not move the camera for it
            unlearned = [n for n in missing if point_spec(_prof, n) is None]
            missing = [n for n in missing if n not in unlearned]
            if not missing:
                self.state.update(status="waiting", missing=unlearned,
                                  detail=f"no click point yet for {unlearned}: use Learn on the status page")
                return
        if _prof is not None:
            # Never keep moving the user's camera for a click that does not work.
            limit = int(self.pcfg.get("max_attempts", 2))
            for n in list(self._fails):
                if self._fail_spec.get(n) != point_spec(_prof, n):
                    self._fails.pop(n, None)       # learned again since
                    self._fail_spec.pop(n, None)
            given_up = [n for n in missing if self._fails.get(n, 0) >= limit]
            missing = [n for n in missing if n not in given_up]
            if not missing:
                self.state.update(status="gave_up", missing=given_up,
                                  detail=f"gave up on {given_up}: the click opened no window {limit} times. "
                                         f"Learn it again, or press Close window to retry")
                return
        if prof_key is None:
            self.state.update(status="waiting", missing=missing,
                              detail=f"no pop-out profile for aircraft '{title}' (profiles: {list(profiles(self.cfg))}; "
                                     f"create one with tools/auto_popout.py --calibrate)")
            return
        self._last_attempt = now
        self.state.update(status="running", detail=f"popping out {missing} ('{title}', profile '{prof_key}')", missing=missing,
                          last_attempt=time.strftime("%H:%M:%S"))
        log.info("auto-popout: %s missing, aircraft '%s' in cockpit -> popping out with profile '%s'", missing, title, prof_key)
        result = ensure_popouts(self.cfg, missing, config_path=self.config_path, say=log.info, aircraft_title=title)
        still = [n for n, h in result.items() if h is None]
        for n in still:
            self._fails[n] = self._fails.get(n, 0) + 1
            self._fail_spec[n] = point_spec(_prof, n) if _prof else None
        self.state.update(status="done" if not still else "partial",
                          detail="all displays popped out" if not still else f"still missing {still}, retry in {self.pcfg['retry_s']} s",
                          missing=still)
