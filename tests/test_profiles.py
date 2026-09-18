"""Profile handling and Pop Out Panel Manager import (no sim needed)."""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))

from glasslink import popout as p  # noqa: E402


class FakeRect:
    def __init__(self, l, t, w, h):
        self.left, self.top, self.width, self.height = l, t, w, h


class FakeSim:
    def __init__(self, w=2560, h=1440):
        self.client = FakeRect(0, 0, w, h)


class ProfileTests(unittest.TestCase):
    def test_builtin_fenix_points_match_measurement(self):
        prof = p.profiles({"popout": {}})["Fenix"]
        pts = p.profile_points(prof, FakeSim())
        self.assertEqual(pts["pfd"], (1237, 1175))        # measured: (1237, 1175) at zoom 30
        self.assertEqual(pts["ecam_lower"], (1917, 1368))

    def test_config_profile_merges_and_selects(self):
        cfg = {"popout": {"profiles": {"Fenix": {"points": {"pfd": [0.5, 0.5]}}, "PMDG": {"zoom": 40, "points": {"pfd": [0.4, 0.8]}}}}}
        profs = p.profiles(cfg)
        self.assertEqual(profs["Fenix"]["points"]["pfd"], [0.5, 0.5])          # overridden
        self.assertEqual(profs["Fenix"]["points"]["nd"], [0.5805, 0.8090])     # kept from built-in
        self.assertEqual(p.select_profile(cfg, "PMDG 737-800 KLM")[0], "PMDG")
        self.assertEqual(p.select_profile(cfg, "FenixA320 CFM SL")[0], "Fenix")
        self.assertIsNone(p.select_profile(cfg, "Cessna 172")[0])

    def test_point_spec_forms_and_camera_keys(self):
        prof = {"camera": {"type": 1, "index": 1}, "points": {
            "pfd": [0.1, 0.2],
            "ecam_upper": {"xy": [0.3, 0.4], "camera": {"type": 2, "index": 7}},
        }}
        self.assertEqual(p.point_spec(prof, "pfd")["camera"], {"type": 1, "index": 1})
        self.assertEqual(p.camera_key(p.point_spec(prof, "pfd")["camera"]), "view:1:1")
        self.assertEqual(p.camera_key(p.point_spec(prof, "ecam_upper")["camera"]), "view:2:7")
        self.assertEqual(p.camera_key({"mode": "reset"}), "reset")
        self.assertIsNone(p.point_spec(prof, "nd"))
        pts = p.profile_points(prof, FakeSim(1000, 500), ["ecam_upper"])
        self.assertEqual(pts, {"ecam_upper": (300, 200)})

    def test_normalise_roundtrip(self):
        sim = FakeSim()
        pts = {"pfd": (1236, 1175)}
        n = p.normalise_points(pts, sim)
        back = p.profile_points({"points": n}, sim)
        self.assertEqual(back["pfd"], (1236, 1175))


class PopmImportTests(unittest.TestCase):
    def test_convert_fenix_profile(self):
        import import_popm

        profile = {
            "Name": "Fenix", "PanelSourceCockpitZoomFactor": 50, "AircraftBindings": ["FNX 320 IAE SL"],
            "PanelConfigs": [
                {"PanelName": "PFD", "PanelSource": {"X": 1226, "Y": 1291}, "FixedCameraConfig": {"CameraType": 1, "CameraIndex": 1, "Name": "Cockpit Pilot"}},
                {"PanelName": "Upper ECAM", "PanelSource": {"X": 1335, "Y": 547}, "FixedCameraConfig": {"CameraType": 2, "CameraIndex": 7, "Name": "Instrument 8"}},
                {"PanelName": "Lower ECAM", "PanelSource": {"X": 1321, "Y": 1134}, "FixedCameraConfig": {"CameraType": 2, "CameraIndex": 7, "Name": "Instrument 8"}},
                {"PanelName": "CPT ND", "PanelSource": {"X": 1562, "Y": 1314}, "FixedCameraConfig": {"CameraType": 1, "CameraIndex": 1, "Name": "Cockpit Pilot"}},
                {"PanelName": "Something", "PanelSource": {"X": 1, "Y": 1}},
            ],
        }
        prof = import_popm.convert(profile, (2560, 1440))
        self.assertEqual(prof["zoom"], 50)
        self.assertEqual(set(prof["points"]), {"pfd", "ecam_upper", "ecam_lower", "nd"})
        self.assertEqual(prof["points"]["pfd"]["xy"], [0.4789, 0.8965])
        self.assertEqual(prof["points"]["ecam_upper"]["camera"]["type"], 2)
        self.assertEqual(prof["points"]["ecam_upper"]["camera"]["index"], 7)
        # the converted profile is usable by the popout code
        pts = p.profile_points(prof, FakeSim())
        self.assertEqual(pts["pfd"], (1226, 1291))        # POPM dot position round-trips exactly
        self.assertEqual(p.camera_key(p.point_spec(prof, "ecam_lower")["camera"]), "view:2:7")


class PopoutDimmingTests(unittest.TestCase):
    def test_home_cockpit_mode_is_read_from_the_settings_file(self):
        import os
        import tempfile
        import time

        from glasslink import popout

        with tempfile.TemporaryDirectory() as d:
            f = os.path.join(d, "persistancy.xml")
            prof = {"popout_dimming": {"file": f, "xml_tag": "homeCockpitMode", "on_value": "true", "name": "HCM"}}
            self.assertIsNone(popout.popout_dims_itself(prof))                  # file missing
            for value, expected in (("false", None), ("true", "HCM"), (" TRUE ", "HCM")):
                open(f, "w").write(f"<fenix><fdLsSync>false</fdLsSync><homeCockpitMode>{value}</homeCockpitMode></fenix>")
                os.utime(f, (time.time() + len(value), time.time() + len(value)))   # make the change visible
                popout._pd_cache.clear()
                self.assertEqual(popout.popout_dims_itself(prof), expected, value)
        self.assertIsNone(popout.popout_dims_itself({}))
        self.assertIsNone(popout.popout_dims_itself(None))


if __name__ == "__main__":
    unittest.main()
