"""Advice: turn what the DMC can observe into troubleshooting steps a builder can act on.

`Advisor.advice(status)` takes the /status dictionary and returns a list of
    {"id", "level" ("warn" | "info"), "title", "detail", "steps": [...], "tab"}
for the status page (and later the tray application). Rules only fire on evidence, and the steps are tailored with
facts about the PC that can be read without touching anything: the graphics card brand, whether the AMD driver's
frame generation is switched on, and the sim's own "glass cockpit refresh rate" and frame generation settings.

Deliberately not here: anything about the stability of a particular PC (memory, overclocking).
"""

from __future__ import annotations

import logging
import os
import re
import time
from typing import Any, Callable

log = logging.getLogger(__name__)

SLOW_FPS = 16.0          # a display in use that receives fewer frames than this from the sim is "slow"
SLOW_FOR_S = 8.0         # ... for this long, so that a hiccup or a loading screen does not count
GLASS_NAMES = {0: "Low", 1: "Medium", 2: "High"}

_GPU_CLASS = r"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"
_USERCFG = (
    r"%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\UserCfg.opt",    # MSFS 2024, Store
    r"%APPDATA%\Microsoft Flight Simulator 2024\UserCfg.opt",                                # MSFS 2024, Steam
    r"%LOCALAPPDATA%\Packages\Microsoft.FlightSimulator_8wekyb3d8bbwe\LocalCache\UserCfg.opt",
    r"%APPDATA%\Microsoft Flight Simulator\UserCfg.opt",
)


def _first_byte(value: Any) -> int | None:
    if isinstance(value, (bytes, bytearray)) and value:
        return value[0]
    if isinstance(value, int):
        return value
    if isinstance(value, str) and value.strip().isdigit():
        return int(value)
    return None


def read_gpu_facts() -> dict[str, Any]:
    """Graphics cards and the AMD driver switches that matter for pop-outs. Read-only registry access."""
    facts: dict[str, Any] = {"gpus": [], "amd": False, "nvidia": False, "amd_frame_gen": None, "amd_chill": None,
                             "amd_frame_limit": None}
    try:
        import winreg
    except ImportError:
        return facts
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, _GPU_CLASS) as cls:
            for i in range(64):
                try:
                    sub = winreg.EnumKey(cls, i)
                except OSError:
                    break
                if not re.fullmatch(r"\d{4}", sub):
                    continue
                try:
                    with winreg.OpenKey(cls, sub) as k:
                        def val(name: str) -> Any:
                            try:
                                return winreg.QueryValueEx(k, name)[0]
                            except OSError:
                                return None

                        desc = str(val("DriverDesc") or "")
                        if not desc or "Basic" in desc:
                            continue
                        facts["gpus"].append(desc)
                        if re.search(r"AMD|Radeon", desc, re.I):
                            facts["amd"] = True
                            for key, name in (("amd_frame_gen", "DrvFrameGenEnabled"), ("amd_chill", "KMD_ChillEnabled"),
                                              ("amd_frame_limit", "KMD_FRTEnabled")):
                                v = _first_byte(val(name))
                                if v is not None:
                                    facts[key] = bool(v) or bool(facts[key])
                        if re.search(r"NVIDIA|GeForce", desc, re.I):
                            facts["nvidia"] = True
                except OSError:
                    continue
    except OSError as exc:
        log.debug("gpu facts: %s", exc)
    return facts


def read_sim_facts(paths: tuple[str, ...] = _USERCFG) -> dict[str, Any]:
    """The sim's own graphics options, from the newest UserCfg.opt (the sim rewrites it when Apply is pressed)."""
    facts: dict[str, Any] = {"glass_refresh": None, "frame_generation": None, "cfg": None}
    found = [p for p in (os.path.expandvars(x) for x in paths) if os.path.isfile(p)]
    if not found:
        return facts
    path = max(found, key=os.path.getmtime)
    try:
        text = open(path, encoding="utf-8", errors="ignore").read()
    except OSError:
        return facts
    facts["cfg"] = path
    m = re.search(r"\{GlassCockpitsRefreshRate\s+Quality\s+(\d+)", text)      # first block = desktop, second = VR
    if m:
        facts["glass_refresh"] = int(m.group(1))
    m = re.search(r"^\s*FrameGeneration\s+(\S+)", text, re.M)
    if m:
        facts["frame_generation"] = m.group(1)
    return facts


def slow_source_steps(gpu: dict[str, Any], sim: dict[str, Any]) -> list[str]:
    """Steps for "the sim delivers too few frames to a pop-out", most likely cause first."""
    steps: list[str] = []
    if gpu.get("amd_frame_gen"):
        steps.append("AMD Fluid Motion Frames is switched ON in your graphics driver, and that is the usual cause: it "
                     "only serves the window in focus and leaves pop-outs with about 13 frames a second. AMD Software > "
                     "Gaming > Graphics: set the global preset to Default (not HYPR-RX), or turn AMD Fluid Motion Frames "
                     "off for the sim.")
    elif gpu.get("amd"):
        steps.append("AMD Software > Gaming > Graphics: check that the preset is Default, not HYPR-RX, and that AMD Fluid "
                     "Motion Frames is off for the sim. Driver frame generation leaves pop-outs with about 13 frames a second.")
    if gpu.get("amd_chill") or gpu.get("amd_frame_limit"):
        steps.append("A frame rate limit is active in the AMD driver (Radeon Chill or Frame Rate Target). The sim shares a "
                     "limit between all its windows: switch it off for the sim.")
    if gpu.get("nvidia"):
        steps.append("NVIDIA app / Control Panel, settings for the sim: Smooth Motion off, Max Frame Rate off. Driver frame "
                     "generation and driver frame limits starve the pop-out windows.")
    if not gpu.get("amd") and not gpu.get("nvidia"):
        steps.append("Graphics driver: switch off driver-level frame generation and any frame rate limit for the sim.")
    glass = sim.get("glass_refresh")
    if glass is not None and glass < 2:
        # Medium was measured to be enough at 47 sim fps (19.5 changed frames a second); it halves with the sim's rate.
        steps.append(f"Sim > Options > General > Graphics: Glass cockpit refresh rate is {GLASS_NAMES.get(glass, glass)}. "
                     f"That is fine with a fast sim, but with the sim below about 40 fps it leaves the instruments under "
                     f"20 updates a second: set it to High.")
    elif glass is None:
        steps.append("Sim > Options > General > Graphics: set Glass cockpit refresh rate to High.")
    steps.append("Check the sim's own frame rate (developer mode FPS counter, or tools/sim_fps.py): a pop-out cannot be "
                 "faster than the sim. Below about 20 fps, lower the graphics settings.")
    steps.append("Judge the result with the sim window in focus: the rate is often fine while another program is in front.")
    return steps


class Advisor:
    def __init__(self, gpu_facts: Callable[[], dict[str, Any]] = read_gpu_facts,
                 sim_facts: Callable[[], dict[str, Any]] = read_sim_facts, clock: Callable[[], float] = time.monotonic) -> None:
        self._gpu_facts, self._sim_facts, self._clock = gpu_facts, sim_facts, clock
        self._facts: dict[str, Any] = {}
        self._facts_t = -1e9
        self._prev: dict[str, tuple[int, float]] = {}       # display -> (received, time)
        self.rates: dict[str, float] = {}                   # display -> frames per second delivered by the sim
        self._slow_since: dict[str, float] = {}

    def facts(self) -> dict[str, Any]:
        now = self._clock()
        if now - self._facts_t > 30.0:
            try:
                self._facts = {"gpu": self._gpu_facts(), "sim": self._sim_facts()}
            except Exception:  # noqa: BLE001 - advice must never break the status page
                log.exception("could not read system facts")
                self._facts = {"gpu": {}, "sim": {}}
            self._facts_t = now
        return self._facts

    # -- measurements ---------------------------------------------------------------------------
    def _track(self, displays: dict[str, Any]) -> list[str]:
        """Update per-display source rates; returns the displays that have been slow for long enough."""
        now = self._clock()
        slow: list[str] = []
        for name, d in displays.items():
            rec = int((d.get("counters") or {}).get("received") or 0)
            prev = self._prev.get(name)
            if prev is None or rec < prev[0]:
                self._prev[name] = (rec, now)
                continue
            if now - prev[1] >= 1.5:
                self.rates[name] = (rec - prev[0]) / (now - prev[1])
                self._prev[name] = (rec, now)
            rate = self.rates.get(name)
            watched = bool(d.get("in_use")) and bool((d.get("window") or {}).get("hwnd")) and float(d.get("capture_fps") or 0) > SLOW_FPS
            if watched and rate is not None and rate < SLOW_FPS:
                self._slow_since.setdefault(name, now)
                if now - self._slow_since[name] >= SLOW_FOR_S:
                    slow.append(name)
            else:
                self._slow_since.pop(name, None)
        for gone in [n for n in self._prev if n not in displays]:
            self._prev.pop(gone, None), self.rates.pop(gone, None), self._slow_since.pop(gone, None)
        return slow

    # -- rules ----------------------------------------------------------------------------------
    def advice(self, status: dict[str, Any]) -> list[dict[str, Any]]:
        out: list[dict[str, Any]] = []
        displays = status.get("displays") or {}
        modules = status.get("modules") or {}
        popout = status.get("popout") or {}
        facts = self.facts()
        slow = self._track(displays)

        if slow:
            rates = ", ".join(f"{n} {self.rates[n]:.0f}" for n in slow)
            out.append({"id": "slow_source", "level": "warn", "tab": "displays",
                        "title": f"The sim delivers only {min(self.rates[n] for n in slow):.0f} frames a second to the displays",
                        "detail": f"Frames per second received: {rates}. A DU cannot show more than the sim hands to its "
                                  f"pop-out window; the aircraft's displays normally change about 20 times a second.",
                        "steps": slow_source_steps(facts.get("gpu") or {}, facts.get("sim") or {})})

        for serial, m in modules.items():
            if not m.get("alive"):
                continue
            who = m.get("label") or serial[:8]
            health = m.get("health") or {}
            if health.get("bad") and m.get("display"):
                out.append({"id": f"du_behind:{serial}", "level": "warn", "tab": "dus",
                            "title": f"{who} is not keeping up with its display",
                            "detail": "; ".join(health.get("reasons") or []),
                            "steps": ["Plug the DU into a USB 2.0 high-speed port or hub of its own; avoid sharing a hub with "
                                      "webcams, audio or storage.",
                                      "Try another cable: a charge-only or very long cable falls back to a slow link.",
                                      "Display units tab: a transfer time (rx) above 15 ms points at the USB link, a decode "
                                      "time above 30 ms at the DU itself (restart it, then update its firmware)."]})
            if m.get("fw_outdated"):
                out.append({"id": f"fw:{serial}", "level": "info", "tab": "dus",
                            "title": f"{who} has older firmware",
                            "detail": "It keeps working, but fixes and new features need the update.",
                            "steps": ["Display units tab: press Update on that DU. It takes about ten seconds and the DU restarts itself."]})
            if not m.get("display"):
                out.append({"id": f"unassigned:{serial}", "level": "info", "tab": "dus",
                            "title": f"{who} is connected but shows nothing",
                            "detail": "No display is assigned to it.",
                            "steps": ["Display units tab: press Identify to see which panel it is, then choose its display."]})

        if status.get("strays"):
            n = len(status["strays"])
            out.append({"id": "strays", "level": "warn", "tab": "setup",
                        "title": f"{n} pop-out window(s) were not made by GlassLink",
                        "detail": "The sim opens each display only once, so a display popped out by hand or by another tool "
                                  "cannot be popped out or learned again. They are often parked off-screen and forgotten.",
                        "steps": ["Setup tab: press Close them. GlassLink then pops the missing displays out itself."]})

        if popout.get("status") == "gave_up":
            names = ", ".join(popout.get("missing") or [])
            out.append({"id": "popout_gave_up", "level": "warn", "tab": "setup",
                        "title": f"Could not pop out: {names}",
                        "detail": "The click opened no window twice, so GlassLink stopped moving your camera for it.",
                        "steps": ["Setup tab: if a banner lists pop-out windows not made by GlassLink, close them first.",
                                  "Press Learn (captain seat), or Learn (FO seat) for an FO side display, and Right-Alt + click "
                                  "the display once when the banner says so.",
                                  "Press Close window on that display to make GlassLink try again with the learned point."]})
        elif "no pop-out profile" in str(popout.get("detail") or "") or "no click point" in str(popout.get("detail") or ""):
            out.append({"id": "popout_unlearned", "level": "info", "tab": "setup",
                        "title": "GlassLink does not know where some displays are in this aircraft",
                        "detail": str(popout.get("detail")),
                        "steps": ["Setup tab: press Learn on each display without a click point and Right-Alt + click it once. "
                                  "The point is stored per aircraft; afterwards the displays pop out by themselves."]})

        br = status.get("brightness") or {}
        if br.get("enabled") and br.get("running") and not br.get("standdown") and not (br.get("map") or {}) and br.get("aircraft"):
            out.append({"id": "no_brightness_profile", "level": "info", "tab": "system",
                        "title": "The cockpit brightness knobs are not linked for this aircraft",
                        "detail": f"No brightness profile for '{br['aircraft']}'. The DUs use their own brightness slider only.",
                        "steps": ["Nothing to do for flying. A profile maps each display to the aircraft's brightness variable; "
                                  "see popout.profiles in the configuration."]})
        return out
