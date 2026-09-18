"""Learn a display's pop-out click point by watching the user pop it out once.

Flow (one display at a time):
  preparing  the camera is put into the aircraft profile's standard view (reset + zoom), exactly the view the
             automatic pop-out will use later, after saving the current view
  waiting    the user Right-Alt+clicks the display in the cockpit, as they would by hand
  adopting   the DMC saw where the click went and which window appeared: it stores the point in the profile
             (as fractions of the sim's client area), names the window GlassLink:<display>, sizes and parks it
  done       the camera is restored; capture of the new window starts by itself

Everything that touches Windows or SimConnect goes through `LearnIO`, so the logic is testable without a sim.
"""

from __future__ import annotations

import logging
import threading
import time
from typing import Any, Callable

log = logging.getLogger(__name__)

WAIT_FOR_CLICK_S = 90.0


class LearnError(ValueError):
    """Cannot start learning; the message is shown to the user."""


class LearnIO:
    """The real world. Tests substitute a fake with the same methods."""

    def sim_window(self):
        from .popout import sim_main_window

        return sim_main_window()

    def popout_windows(self) -> set[int]:
        from . import windows as win
        from .popout import SIM_CLASS, SIM_PROCESS

        return win.hwnd_set(SIM_PROCESS, SIM_CLASS)

    def stray_popouts(self) -> list[dict[str, Any]]:
        from .popout import stray_popouts

        return [{"hwnd": w.hwnd, "title": w.title, "size": list(w.client.size), "position": [w.rect.left, w.rect.top]}
                for w in stray_popouts()]

    def close_strays(self) -> int:
        import win32con
        import win32gui

        from .popout import stray_popouts

        ws = stray_popouts()
        for w in ws:
            win32gui.PostMessage(w.hwnd, win32con.WM_CLOSE, 0, 0)
        if ws:
            time.sleep(1.0)
        return len(ws)

    def cursor(self) -> tuple[int, int]:
        import win32api

        return win32api.GetCursorPos()

    def ralt_click_down(self) -> bool:
        import win32api
        import win32con

        return bool(win32api.GetAsyncKeyState(win32con.VK_RMENU) & 0x8000) and \
            bool(win32api.GetAsyncKeyState(win32con.VK_LBUTTON) & 0x8000)

    def open_camera(self):
        from .popout import try_sim_camera

        return try_sim_camera()

    def prepare_camera(self, cam, sim, prof: dict[str, Any], pcfg: dict[str, Any], say,
                       camspec: dict[str, Any] | None = None, save_current: bool = False) -> Callable[[], None]:
        """Put the camera into the view the point will be stored for; returns a function that undoes it.
        `save_current`: the user's present view becomes that view (saved as a sim custom camera), nothing moves."""
        from .popout import apply_camera, custom_camera

        camspec = camspec or prof.get("camera") or {"mode": "reset"}
        old_zoom = cam.zoom
        restore = pcfg.get("camera_restore")
        if restore == "current":
            say(f"saving the current view to custom camera {pcfg['camera_slot']}")
            custom_camera(sim.hwnd, int(pcfg["camera_slot"]), save=True)
            time.sleep(0.5)
        if camspec.get("mode") == "custom" and save_current:
            say(f"saving your view as the pop-out view (custom camera {camspec['slot']})")
            custom_camera(sim.hwnd, int(camspec["slot"]), save=True)
            time.sleep(0.5)
        else:
            apply_camera(cam, camspec, float(prof.get("zoom", pcfg["zoom"])), say, sim.hwnd)

        def undo() -> None:
            try:
                cam.reset()
                if old_zoom is not None:
                    time.sleep(0.5)
                    cam.zoom = old_zoom
                slot = pcfg["camera_slot"] if restore == "current" else restore
                if slot not in (None, ""):
                    time.sleep(0.5)
                    custom_camera(sim.hwnd, int(slot), save=False)
            finally:
                cam.close()

        return undo

    def close_existing(self, name: str) -> bool:
        """Close the display's current pop-out window, if any, so learning again does not leave a duplicate."""
        import win32con
        import win32gui

        from .popout import popout_exists

        w = popout_exists(name)
        if w is None:
            return False
        win32gui.PostMessage(w.hwnd, win32con.WM_CLOSE, 0, 0)
        time.sleep(1.0)
        return True

    def adopt(self, hwnd: int, name: str, dcfg: dict[str, Any]) -> None:
        from . import windows as win
        from .popout import SIM_CLASS, SIM_PROCESS

        win.rename_window(hwnd, f"GlassLink:{name}")
        dcfg["match"] = {"process": SIM_PROCESS, "class": SIM_CLASS, "title": f"GlassLink:{name}"}
        size, pos = dcfg.get("client_size"), dcfg.get("position")
        if size:
            px, py = (pos or (None, None))
            win.set_client_size(hwnd, int(size[0]), int(size[1]), px, py)
        elif pos:
            win.move_window(hwnd, int(pos[0]), int(pos[1]))


def normalise(point: tuple[int, int], client) -> list[float] | None:
    """Screen point -> fractions of the sim's client area (4 decimals); None if it lies outside."""
    fx = (point[0] - client.left) / max(1, client.width)
    fy = (point[1] - client.top) / max(1, client.height)
    if not (0.0 <= fx <= 1.0 and 0.0 <= fy <= 1.0):
        return None
    return [round(fx, 4), round(fy, 4)]


def watch_for_popout(io: Any, before: set[int], timeout_s: float, stop: threading.Event,
                     poll_s: float = 0.02) -> tuple[int | None, tuple[int, int] | None]:
    """Wait for a new pop-out window. Returns (hwnd, click position).

    The click position is where the cursor was while Right-Alt + left button were down. If the press was too short to
    be seen, the cursor position when the window appeared is used instead (people rarely move within that half second).
    """
    click: tuple[int, int] | None = None
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline and not stop.is_set():
        if io.ralt_click_down():
            click = io.cursor()
        new = io.popout_windows() - before
        if new:
            return sorted(new)[0], (click or io.cursor())
        time.sleep(poll_s)
    return None, click


class PopoutLearner:
    def __init__(self, cfg: dict[str, Any], config_path: Any = None, io: Any = None,
                 pause_auto: Callable[[bool], None] | None = None,
                 save: Callable[[dict[str, Any], Any], Any] | None = None) -> None:
        from .config import save_config

        self.cfg = cfg
        self.config_path = config_path
        self.io = io or LearnIO()
        self._pause_auto = pause_auto or (lambda paused: None)
        self._save = save or save_config
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self.state: dict[str, Any] = {"status": "idle", "display": None, "detail": "", "point": None}

    @property
    def busy(self) -> bool:
        return self._thread is not None and self._thread.is_alive()

    def start(self, name: str, view: str = "standard") -> None:
        """view: "standard" = the profile's seat view; "mine" = the view the user is looking from right now."""
        if view not in ("standard", "mine"):
            raise LearnError("view must be 'standard' or 'mine'")
        if self.busy:
            raise LearnError(f"already learning '{self.state.get('display')}'")
        if name not in self.cfg.get("displays", {}):
            raise LearnError(f"unknown display '{name}'")
        if self.io.sim_window() is None:
            raise LearnError("the simulator is not running")
        self._stop.clear()
        self.state.update(status="preparing", display=name, detail="setting the camera", point=None)
        self._thread = threading.Thread(target=self._run, args=(name, view), name="popout-learn", daemon=True)
        self._thread.start()

    def cancel(self) -> None:
        self._stop.set()

    def _say(self, text: str) -> None:
        log.info("learn: %s", text)
        self.state["detail"] = text

    def _run(self, name: str, view: str = "standard") -> None:
        from .popout import POPOUT_DEFAULTS, camera_key, point_spec, select_profile

        undo: Callable[[], None] | None = None
        self._pause_auto(True)
        try:
            sim = self.io.sim_window()
            cam = self.io.open_camera()
            if sim is None or cam is None:
                raise LearnError("SimConnect is not available (is the sim in a flight?)")
            if not cam.in_cockpit:
                cam.close()
                raise LearnError("the sim is not in the cockpit view")
            title = cam.title
            pcfg = {**POPOUT_DEFAULTS, **self.cfg.get("popout", {})}
            key, prof = select_profile(self.cfg, title)
            if prof is None:                      # first display of an aircraft nobody has a profile for yet
                key = title.strip() or "aircraft"
                prof = {"zoom": pcfg["zoom"], "points": {}}
            camspec = prof.get("camera") or {"mode": "reset"}
            save_current = False
            if view == "mine":
                camspec = {"mode": "custom", "slot": int(pcfg.get("view_slot", 8))}
                others = [n for n in (prof.get("points") or {}) if n != name
                          and camera_key((point_spec(prof, n) or {}).get("camera") or {}) == camera_key(camspec)]
                # The first display learned this way defines the view. Later ones are learned in that same saved
                # view, so that one camera move serves them all at pop-out time.
                save_current = not others
                if others:
                    self._say(f"using the view saved for {others}")
            undo = self.io.prepare_camera(cam, sim, prof, pcfg, self._say, camspec, save_current)
            sim = self.io.sim_window() or sim
            if self.io.close_existing(name):
                self._say(f"closed the previous {name} window")
            before = self.io.popout_windows()
            strays = self.io.stray_popouts() if hasattr(self.io, "stray_popouts") else []
            note = "" if not strays else (f" - note: {len(strays)} pop-out window(s) not made by GlassLink are open; if one "
                                          f"of them is this display, your click opens nothing: cancel and close them first")
            self.state.update(status="waiting",
                              detail=f"Right-Alt + click the {name} display in the cockpit now ({int(WAIT_FOR_CLICK_S)} s){note}")
            hwnd, click = watch_for_popout(self.io, before, WAIT_FOR_CLICK_S, self._stop)
            if hwnd is None:
                self.state.update(status="cancelled" if self._stop.is_set() else "timeout",
                                  detail="cancelled" if self._stop.is_set() else "no new pop-out window appeared"
                                  + (" (a pop-out not made by GlassLink is open: close it under Setup and try again)" if strays else ""))
                return
            self.state.update(status="adopting", detail="storing the click point and parking the window")
            point = normalise(click, sim.client) if click else None
            if point is None:
                raise LearnError("the click was outside the simulator window; nothing stored")
            user_prof = self.cfg.setdefault("popout", {}).setdefault("profiles", {}).setdefault(key, {})
            user_prof.setdefault("points", {})[name] = point if camspec.get("mode") != "custom" \
                else {"xy": point, "camera": dict(camspec)}
            if "zoom" not in user_prof and "zoom" in prof:
                user_prof["zoom"] = prof["zoom"]
            self.io.adopt(hwnd, name, self.cfg["displays"][name])
            self._save(self.cfg, self.config_path)
            self.state.update(status="done", point=point,
                              detail=f"learned {name} at {point} for profile '{key}'; the window is parked")
            log.info("learn: %s", self.state["detail"])
        except LearnError as exc:
            self.state.update(status="error", detail=str(exc))
        except Exception as exc:  # noqa: BLE001
            log.exception("learning '%s' failed", name)
            self.state.update(status="error", detail=f"{type(exc).__name__}: {exc}")
        finally:
            if undo is not None:
                try:
                    undo()
                except Exception:  # noqa: BLE001
                    log.exception("could not restore the camera")
            self._pause_auto(False)
