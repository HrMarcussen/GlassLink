"""Advice rules: they fire on evidence only, and the steps follow what the PC looks like."""

from __future__ import annotations

import unittest

from glasslink import advisor


class Clock:
    def __init__(self) -> None:
        self.t = 1000.0

    def __call__(self) -> float:
        return self.t


def display(received: int, in_use: bool = True, hwnd: int = 1, cap: float = 40.0) -> dict:
    return {"counters": {"received": received}, "in_use": in_use, "window": {"hwnd": hwnd}, "capture_fps": cap}


class AdvisorTests(unittest.TestCase):
    def make(self, gpu=None, sim=None):
        self.clock = Clock()
        return advisor.Advisor(gpu_facts=lambda: gpu or {}, sim_facts=lambda: sim or {}, clock=self.clock)

    def run_rate(self, adv, fps: float, seconds: float, **kw) -> list[dict]:
        out, rec = [], 0
        for _ in range(int(seconds / 2)):
            self.clock.t += 2.0
            rec += int(fps * 2)
            out = adv.advice({"displays": {"pfd": display(rec, **kw)}})
        return out

    def test_a_throttled_display_in_use_is_reported_after_a_while_not_at_once(self):
        adv = self.make(gpu={"amd": True, "amd_frame_gen": True}, sim={"glass_refresh": 1})
        self.assertEqual(self.run_rate(adv, 13, 6), [])                      # too early
        found = self.run_rate(adv, 13, 10)
        self.assertEqual([a["id"] for a in found], ["slow_source"])
        steps = " ".join(found[0]["steps"])
        self.assertIn("AMD Fluid Motion Frames is switched ON", found[0]["steps"][0])     # the proven cause comes first
        self.assertIn("Glass cockpit refresh rate is Medium", steps)
        self.assertIn("window in focus", steps)
        self.assertNotIn("NVIDIA", steps)
        self.assertEqual(self.run_rate(adv, 30, 4), [])                      # and it goes away when the rate is back

    def test_healthy_idle_and_missing_displays_are_not_slow(self):
        adv = self.make()
        self.assertEqual(self.run_rate(adv, 30, 20), [])
        self.assertEqual(self.run_rate(self.make(), 1, 20, in_use=False, cap=1.0), [])    # preview rate of an unused display
        self.assertEqual(self.run_rate(self.make(), 0, 20, hwnd=0), [])                   # no window: another rule's business

    def test_steps_follow_the_graphics_card(self):
        nv = " ".join(advisor.slow_source_steps({"nvidia": True}, {"glass_refresh": 2}))
        self.assertIn("Smooth Motion", nv)
        self.assertNotIn("Glass cockpit refresh rate is", nv)                # already High
        amd_off = advisor.slow_source_steps({"amd": True, "amd_frame_gen": False}, {})
        self.assertIn("check that the preset is Default", amd_off[0])
        unknown = " ".join(advisor.slow_source_steps({}, {}))
        self.assertIn("driver-level frame generation", unknown)
        limit = " ".join(advisor.slow_source_steps({"amd": True, "amd_chill": True}, {}))
        self.assertIn("frame rate limit is active", limit)

    def test_module_and_popout_rules(self):
        adv = self.make()
        status = {"displays": {}, "strays": [{"hwnd": 5}],
                  "popout": {"status": "gave_up", "missing": ["fo_nd"]},
                  "modules": {"aa11bb22cc": {"alive": True, "label": "DU1", "display": "pfd", "fw_outdated": True,
                                             "health": {"bad": True, "reasons": ["rx_ms 22"]}},
                              "dd44": {"alive": True, "label": "", "display": ""},
                              "ee55": {"alive": False, "display": "", "fw_outdated": True}}}
        ids = [a["id"] for a in adv.advice(status)]
        self.assertEqual(ids, ["du_behind:aa11bb22cc", "fw:aa11bb22cc", "unassigned:dd44", "strays", "popout_gave_up"])
        for a in adv.advice(status):
            self.assertTrue(a["title"] and a["steps"] and a["level"] in ("warn", "info") and a["tab"])

    def test_sim_settings_are_read_from_usercfg(self):
        import os
        import tempfile

        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "UserCfg.opt")
            with open(path, "w", encoding="utf-8") as f:
                f.write("{Graphics\n\tFrameGeneration FSRFG\n\t{GlassCockpitsRefreshRate\n\t\tQuality 1\n\t}\n}\n"
                        "{GraphicsVR\n\t{GlassCockpitsRefreshRate\n\t\tQuality 2\n\t}\n}\n")
            facts = advisor.read_sim_facts((path, os.path.join(d, "missing.opt")))
        self.assertEqual((facts["glass_refresh"], facts["frame_generation"]), (1, "FSRFG"))      # desktop block, not VR
        self.assertEqual(advisor.read_sim_facts((os.path.join(d, "nope"),))["glass_refresh"], None)


if __name__ == "__main__":
    unittest.main()
