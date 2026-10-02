# Troubleshooting

Things that are not GlassLink's fault but look like it. Each entry: what you see, why, what to do.
The status page shows the matching advice by itself when the DMC notices one of these (panel at the top of the page).

## The displays are jerky: "slow source" on the status page

**What you see.** A DU runs at 10 to 13 fps although the sim itself runs at 40 or more. The status page shows
`slow source · 13 frames/s` on the display's card and `Displays 6/6 · slow source` in the top bar.

**Why.** The sim hands every pop-out window its frames; GlassLink can only pass on what arrives. Three things cut
that rate:

1. **Frame generation in the graphics driver.** AMD's *HYPR-RX* preset switches on *AMD Fluid Motion Frames* (and
   Anti-Lag, Boost, Super Resolution); NVIDIA has *Smooth Motion*. Driver frame generation works for the window in
   focus only and takes over frame pacing; the sim's other windows, the pop-outs, are left with about 13 frames a
   second. Tell-tale: the rate is fine while another program is in front and drops the moment you click into the sim.
   Measured on an RX 7900 XTX: 13 fps with HYPR-RX, 27 to 30 fps with the preset on *Default*.
   **Fix:** AMD Adrenalin, Gaming, Graphics: global preset *Default* (or a profile for the sim with Fluid Motion
   Frames off). The sim's own frame generation (FSR / DLSS in the sim's graphics options) is fine: 30 fps measured.
2. **A frame rate limit in the driver** (AMD Chill / Frame Rate Target, NVIDIA Max Frame Rate). The sim shares a
   limit between all of its windows. **Fix:** no driver limit for the sim; use the sim's own limiter if you need one.
3. **The sim below about 20 fps.** Pop-outs cannot be faster than the sim.

The sim's *Glass cockpit refresh rate* (Options, General, Graphics) decides how often instruments are redrawn: High =
every sim frame. Measured on 20 Sept 2026 with the Fenix on **Medium**, sim at about 47 fps, a 30 minute flight: the
PFD picture changed 19.5 times a second (median; 17.3 to 19.8 for 80 % of the time) and the DU showed 19.6 fps, the
same as on High, while the sim itself ran noticeably faster. Medium is fine as long as the sim stays above about
40 fps; on Low, or with a slow sim, expect fewer. Most airliner add-ons draw their displays at about 20 Hz anyway (the Fenix does), so more than that
never reaches a DU.

**Always measure with the sim window in focus.** `tools/sim_fps.py` prints the sim's frame rate, a pop-out's frame
rate and the window in front, side by side. `tools/content_fps.py` shows how many of those frames actually differ,
which is what a DU displays; it needs a moving aircraft.

## A DU shows fewer than 20 pictures a second: "shows N fps" on the status page

**What you see.** The Display units tab or the advice panel says the DU "shows 15 fps: its pictures come faster than
it draws them". Unlike a slow source, the sim delivers enough; the DU is the limit.

**Why.** A DU's work grows with the pixels it draws. In 1080p it also shares its memory with the picture it scans out
to the monitor, so a large picture that has to be copied into place is the slowest case.

**What to do.** Update the DU if the status page offers it (from 0.6.0 several displays on one DU go as one picture,
twice as fast in 1080p). Make the display's picture smaller (its size on the Displays tab), put fewer displays on
that DU, or use a lower HDMI mode if the screen allows it.

## The displays are not popped out and the brightness knobs do nothing: SimConnect not available

GlassLink needs Microsoft's `SimConnect.dll`. Releases include it; a checkout, or a release built without it, does
not. Without it the status page says "SimConnect.dll is missing" (System tab and the advice panel).
Install the MSFS SDK (Developer Mode in the sim's options, then Help > SDK Installer), or copy
`SimConnect SDK\lib\SimConnect.dll` from an SDK to `%LOCALAPPDATA%\GlassLink\`, and restart GlassLink. Streaming to
the DUs works without it; you then pop the displays out by hand.

## Key presses do not reach the sim after switching back to it

**Why.** Windows gives the focus to the sim's most recently active window, and that used to be the last pop-out,
parked off-screen. **Fixed** after 0.4.0: GlassLink marks its pop-out windows as never-activated. Pop-outs made by
hand or by another tool do not have that mark; the Setup tab lists them and can close them.

## A display will not pop out / Learn sees no click

The display is already popped out somewhere, often off-screen from an earlier session: the sim opens each display
once. The Setup tab shows "pop-out windows not made by GlassLink" with a button to close them.

## X-Plane: a ToLiss display does not get a window

- The status page says "open inside X-Plane, not as a window of its own": the popup is open, but inside X-Plane's
  screen. Pop it out once with the button at the right end of its title bar (the red dot on the left closes it).
  With the ISCS options "Use popout windows for popups" and "Save popup config on quit" on (ToLiss ISCS, Settings
  tab, "User interface"; each ToLiss aircraft has its own), the aircraft remembers it and GlassLink opens it as a
  window from then on.
- The status page says "Sim not running" while X-Plane runs: GlassLink talks to X-Plane through its web API
  (`localhost:8086`), which needs X-Plane 12.1.4 or later and is switched off by X-Plane's network setting "Disable
  incoming traffic" and by the command-line option `--no_web_server`.
- A close message sent to an X-Plane pop-out by another program (a window tool, a script) makes X-Plane quit at once.
  GlassLink's "Close window" closes a ToLiss pop-out with the aircraft's own command instead.

## The pop-outs do not dim with the cockpit knobs

In current sim and Fenix versions a pop-out is only on or off under the brightness knob, never dimmed; GlassLink
reads the knobs over SimConnect and dims the DU. Exception: with the Fenix EFB setting *Home Cockpit Mode* on, the
pop-outs do dim themselves, and GlassLink stands down ("dimmed by the aircraft" on the System tab). The FSLabs always
dims its pop-outs itself, so GlassLink never dims for it, and so does the ToLiss in X-Plane.

## Blue screens or sim crashes while testing

GlassLink has no kernel driver (the DUs use Microsoft's WinUSB), so it cannot cause a blue screen by itself. It does
keep the GPU driver and the memory busy, which brings out instability that is already there: memory running above
what the CPU is rated for (XMP), automatic CPU overclocking. Check those first. Never end the DMC with Task Manager
while the sim runs; use Quit in its tray menu or `GlassLink.exe --quit` (ending a process that holds window captures
can upset the GPU driver).

## Windows shows an old name for a DU

Windows remembers a USB device's name. Remove the device once in Device Manager (with the DU plugged in) and plug it
in again.
