"""Display editor: registry (add / change / remove at runtime) and the pop-out learn flow, without a simulator."""

from __future__ import annotations

import threading
import time
import unittest
from types import SimpleNamespace

from glasslink import learn, registry


class FakeHub:
    def __init__(self) -> None:
        self.displays: dict[str, object] = {}

    def add(self, name):
        self.displays[name] = SimpleNamespace(name=name)
        return self.displays[name]

    def get(self, name):
        return self.displays.get(name)

    def remove(self, name):
        self.displays.pop(name, None)


class FakeWorker:
    def __init__(self, name, dcfg):
        self.name, self.dcfg, self.started, self.stopped = name, dict(dcfg), False, False

    def start(self):
        self.started = True

    def stop(self):
        self.stopped = True


class RegistryTests(unittest.TestCase):
    def setUp(self) -> None:
        self.cfg = {"displays": {"pfd": {"match": {"process": registry.SIM_PROCESS}, "client_size": [768, 768],
                                         "position": [2600, 0]}}, "modules": {}}
        self.saved = 0
        self.made: list[FakeWorker] = []

        def factory(name, dcfg):
            w = FakeWorker(name, dcfg)
            self.made.append(w)
            return w

        def save(cfg, path):
            self.saved += 1

        self.hub = FakeHub()
        self.reg = registry.DisplayRegistry(self.cfg, self.hub, factory, None, save=save)
        self.reg.start_all()

    def test_add_gets_title_size_and_a_free_parking_slot(self):
        d = self.reg.add("fo_pfd")
        self.assertEqual(d["match"]["title"], "GlassLink:fo_pfd")
        self.assertEqual(d["client_size"], [768, 768])
        self.assertEqual(d["position"], [3400, 0])                 # 2600,0 is taken by pfd
        self.assertEqual(self.reg.add("fo_nd")["position"], [4200, 0])
        self.assertIn("fo_pfd", self.hub.displays)
        self.assertTrue(self.reg.workers["fo_pfd"].started)
        self.assertEqual(self.saved, 2)

    def test_names_are_validated_and_unique(self):
        for bad in ("", "1pfd", "FO PFD", "pfd!", "x" * 30):
            with self.assertRaises(registry.DisplayError, msg=bad):
                self.reg.add(bad)
        with self.assertRaises(registry.DisplayError):
            self.reg.add("pfd")
        self.assertEqual(self.reg.add("  FO_ND ")["match"]["title"], "GlassLink:fo_nd")   # trimmed, lower-cased

    def test_update_restarts_the_worker_and_validates(self):
        first = self.reg.workers["pfd"]
        d = self.reg.update("pfd", {"client_size": [800, 800], "fps": "12", "quality": None, "bogus": 1})
        self.assertEqual((d["client_size"], d["fps"]), ([800, 800], 12.0))
        self.assertNotIn("bogus", d)
        self.assertTrue(first.stopped)
        self.assertIsNot(self.reg.workers["pfd"], first)
        for bad in ({"fps": 500}, {"client_size": [1, 2]}, {"client_size": "wide"}, {"quality": 5}):
            with self.assertRaises(registry.DisplayError, msg=str(bad)):
                self.reg.update("pfd", bad)
        with self.assertRaises(registry.DisplayError):
            self.reg.update("nope", {"fps": 10})

    def test_remove_stops_forgets_and_tells_the_module_manager(self):
        gone = []
        self.reg.on_removed = gone.append
        w = self.reg.workers["pfd"]
        self.reg.remove("pfd")
        self.assertTrue(w.stopped)
        self.assertNotIn("pfd", self.cfg["displays"])
        self.assertNotIn("pfd", self.hub.displays)
        self.assertEqual(gone, ["pfd"])
        with self.assertRaises(registry.DisplayError):
            self.reg.remove("pfd")

    def test_describe_marks_learned_points(self):
        self.reg.add("fo_pfd")
        info = self.reg.describe({"pfd": [0.48, 0.81]})
        self.assertTrue(info["pfd"]["has_point"])
        self.assertFalse(info["fo_pfd"]["has_point"])
        self.assertTrue(info["fo_pfd"]["sim_window"])


class FakeCamera:
    in_cockpit = True
    title = "FenixA320 CFM SL"
    zoom = 50.0

    def close(self):
        pass


class FakeIO:
    """A sim whose client area is 2560x1440 at 0,0. `script` decides when the click and the window happen."""

    def __init__(self, click_at=(1237, 1175), window_after=0.15, hold=True, in_cockpit=True) -> None:
        self.client = SimpleNamespace(left=0, top=0, width=2560, height=1440)
        self.t0 = time.monotonic()
        self.click_at, self.window_after, self.hold = click_at, window_after, hold
        self.cam = FakeCamera()
        self.cam.in_cockpit = in_cockpit
        self.adopted: list[tuple[int, str]] = []
        self.prepared: list[tuple[dict, bool]] = []
        self.undone = False
        self.cursor_pos = (100, 100)

    def _elapsed(self):
        return time.monotonic() - self.t0

    def sim_window(self):
        return SimpleNamespace(hwnd=1, client=self.client)

    def popout_windows(self):
        base = {10, 11}
        return base | ({99} if self.window_after is not None and self._elapsed() > self.window_after else set())

    def cursor(self):
        # the user is at the display while clicking, and has wandered off by the time the window shows up
        return self.click_at if self._elapsed() < 0.1 else (2000, 300)

    def ralt_click_down(self):
        return self.hold and 0.03 < self._elapsed() < 0.1

    def open_camera(self):
        return self.cam

    def prepare_camera(self, cam, sim, prof, pcfg, say, camspec=None, save_current=False):
        say("camera set")
        self.prepared.append((dict(camspec or {}), save_current))
        return lambda: setattr(self, "undone", True)

    def close_existing(self, name):
        return False

    def adopt(self, hwnd, name, dcfg):
        self.adopted.append((hwnd, name))
        dcfg["match"] = {"title": f"GlassLink:{name}"}


class LearnTests(unittest.TestCase):
    def _learner(self, io, displays=("fo_pfd",)):
        cfg = {"displays": {n: {"client_size": [768, 768], "position": [3400, 0]} for n in displays}, "popout": {}}
        self.paused: list[bool] = []
        self.saves = 0

        def save(c, p):
            self.saves += 1

        return cfg, learn.PopoutLearner(cfg, None, io=io, pause_auto=self.paused.append, save=save)

    def _finish(self, lr, timeout=5.0):
        t0 = time.time()
        while lr.busy and time.time() - t0 < timeout:
            time.sleep(0.02)
        self.assertFalse(lr.busy)

    def test_normalise(self):
        client = SimpleNamespace(left=100, top=50, width=2000, height=1000)
        self.assertEqual(learn.normalise((1100, 550), client), [0.5, 0.5])
        self.assertIsNone(learn.normalise((50, 550), client))          # left of the sim window
        self.assertIsNone(learn.normalise((1100, 1200), client))       # below it

    def test_click_position_wins_over_where_the_cursor_is_later(self):
        io = FakeIO()
        hwnd, click = learn.watch_for_popout(io, {10, 11}, 3.0, threading.Event(), poll_s=0.005)
        self.assertEqual((hwnd, click), (99, (1237, 1175)))

    def test_falls_back_to_the_cursor_if_the_press_was_not_seen(self):
        io = FakeIO(hold=False, window_after=0.05)
        hwnd, click = learn.watch_for_popout(io, {10, 11}, 3.0, threading.Event(), poll_s=0.005)
        self.assertEqual(hwnd, 99)
        self.assertEqual(click, (1237, 1175))                          # still at the display 50 ms after the click

    def test_learning_stores_the_point_adopts_the_window_and_restores_the_camera(self):
        io = FakeIO()
        cfg, lr = self._learner(io)
        lr.start("fo_pfd")
        self._finish(lr)
        self.assertEqual(lr.state["status"], "done", lr.state)
        self.assertEqual(cfg["popout"]["profiles"]["Fenix"]["points"]["fo_pfd"], [0.4832, 0.816])
        self.assertEqual(io.adopted, [(99, "fo_pfd")])
        self.assertTrue(io.undone)
        self.assertEqual(self.paused, [True, False])                   # auto pop-out paused for the duration
        self.assertEqual(self.saves, 1)

    def test_learning_from_the_fo_seat_stores_the_copilot_view_with_the_point(self):
        io = FakeIO(click_at=(2001, 850))
        cfg, lr = self._learner(io, displays=("fo_pfd", "fo_nd"))
        lr.start("fo_pfd", "copilot")
        self._finish(lr)
        self.assertEqual(lr.state["status"], "done", lr.state)
        view = {"mode": "view", "type": 1, "index": 4}
        self.assertEqual(cfg["popout"]["profiles"]["Fenix"]["points"]["fo_pfd"], {"xy": [0.7816, 0.5903], "camera": view})
        self.assertEqual(io.prepared[-1][0], view)
        with self.assertRaises(learn.LearnError):
            lr.start("fo_nd", "sideways")

    def test_fo_seat_points_are_popped_out_in_their_own_camera_group(self):
        from glasslink import popout

        prof = {"points": {"pfd": [0.48, 0.81],
                           "fo_pfd": {"xy": [0.78, 0.59], "camera": {"mode": "view", "type": 1, "index": 4}},
                           "old": {"xy": [0.5, 0.5], "camera": {"mode": "custom", "slot": 8}}}}
        self.assertEqual(popout.camera_key(popout.point_spec(prof, "pfd")["camera"]), "reset")
        self.assertEqual(popout.camera_key(popout.point_spec(prof, "fo_pfd")["camera"]), "view:1:4")
        self.assertIsNone(popout.point_spec(prof, "old"))          # sim custom cameras are no longer used: learn again

    def test_timeout_and_errors_leave_the_config_alone(self):
        old = learn.WAIT_FOR_CLICK_S
        learn.WAIT_FOR_CLICK_S = 0.2
        try:
            io = FakeIO(window_after=None)
            cfg, lr = self._learner(io)
            lr.start("fo_pfd")
            self._finish(lr)
            self.assertEqual(lr.state["status"], "timeout")
            self.assertTrue(io.undone)
            self.assertNotIn("profiles", cfg["popout"])
        finally:
            learn.WAIT_FOR_CLICK_S = old
        io = FakeIO(in_cockpit=False)
        cfg, lr = self._learner(io)
        lr.start("fo_pfd")
        self._finish(lr)
        self.assertEqual(lr.state["status"], "error")
        self.assertIn("cockpit", lr.state["detail"])
        with self.assertRaises(learn.LearnError):
            lr.start("unknown_display")

    def test_a_click_outside_the_sim_is_refused(self):
        io = FakeIO(click_at=(5000, 100))
        cfg, lr = self._learner(io)
        lr.start("fo_pfd")
        self._finish(lr)
        self.assertEqual(lr.state["status"], "error")
        self.assertEqual(io.adopted, [])
        self.assertNotIn("profiles", cfg["popout"])


if __name__ == "__main__":
    unittest.main()
