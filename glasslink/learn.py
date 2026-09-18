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

    def prepare_camera(self, cam, sim, prof: dict[str, Any], pcfg: dict[str, Any], say) -> Callable[[], None]:
        """Put the camera into the profile's view; returns a function that undoes it."""
        from .popout import apply_camera, custom_camera

        old_zoom = cam.zoom
        restore = pcfg.get("camera_restore")
        if restore == "current":
            say(f"saving the current view to custom camera {pcfg['camera_slot']}")
            custom_camera(sim.hwnd, int(pcfg["camera_slot"]), save=True)
            time.sleep(0.5)
        apply_camera(cam, prof.get("camera") or {"mode": "reset"}, float(prof.get("zoom", pcfg["zoom"])), say)

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

    def start(self, name: str) -> None:
        if self.busy:
            raise LearnError(f"already learning '{self.state.get('display')}'")
        if name not in self.cfg.get("displays", {}):
            raise LearnError(f"unknown display '{name}'")
        if self.io.sim_window() is None:
            raise LearnError("the simulator is not running")
        self._stop.clear()
        self.state.update(status="preparing", display=name, detail="setting the camera", point=None)
        self._thread = threading.Thread(target=self._run, args=(name,), name="popout-learn", daemon=True)
        self._thread.start()

    def cancel(self) -> None:
        self._stop.set()

    def _say(self, text: str) -> None:
        log.info("learn: %s", text)
        self.state["detail"] = text

    def _run(self, name: str) -> None:
        from .popout import POPOUT_DEFAULTS, select_profile

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
            undo = self.io.prepare_camera(cam, sim, prof, pcfg, self._say)
            sim = self.io.sim_window() or sim
            before = self.io.popout_windows()
            self.state.update(status="waiting",
                              detail=f"Right-Alt + click the {name} display in the cockpit now ({int(WAIT_FOR_CLICK_S)} s)")
            hwnd, click = watch_for_popout(self.io, before, WAIT_FOR_CLICK_S, self._stop)
            if hwnd is None:
                self.state.update(status="cancelled" if self._stop.is_set() else "timeout",
                                  detail="cancelled" if self._stop.is_set() else "no new pop-out window appeared")
                return
            self.state.update(status="adopting", detail="storing the click point and parking the window")
            point = normalise(click, sim.client) if click else None
            if point is None:
                raise LearnError("the click was outside the simulator window; nothing stored")
            user_prof = self.cfg.setdefault("popout", {}).setdefault("profiles", {}).setdefault(key, {})
            user_prof.setdefault("points", {})[name] = point
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
