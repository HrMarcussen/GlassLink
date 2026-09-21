# Changelog

All notable changes to GlassLink. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow [Semantic Versioning](https://semver.org/) with one version for the whole repository:

- The file `VERSION` is the single source of truth. The DMC (`glasslink.__version__`, status page) and the DU
  firmware (`fw` in the INFO message, shown per DU on the status page) are both built from it, and each reports
  the git commit it was built from as `build`.
- While the project is 0.x: **minor** (0.2 -> 0.3) for new features or any change to the USB protocol or the
  config format, **patch** (0.2.0 -> 0.2.1) for fixes and internal work. 1.0.0 is reserved for the first version
  that runs a full cockpit day to day.
- Every released version is a git tag `vX.Y.Z`. Work in progress is collected under "Unreleased".
- Entries that need the DUs reflashed say so: **[DU firmware]**, and such a release also sets `FIRMWARE_VERSION`
  to its own number. The status page marks a DU as outdated only if its firmware is older than that, so releases
  that do not touch `firmware/` never ask for a reflash. A DU on older firmware keeps working as long as the
  protocol version (currently 1) is unchanged.

## [Unreleased]

### Added
- **Two DUs on one hub, verified 21 Sept 2026** (DU2 flashed with 0.5.0 over its serial port, both on the DMC):
  each DU on its own display 29.5 and 30 fps of a 30 fps source, both on the same display 29.5 and 28, assignments
  swapped under load 29.5 and 29, no frames dropped, transfer 3.8-4.0 ms per frame with both running (the same as
  one alone), decode 6 ms, draw 12-13 ms; DMC 19 % of one core for two displays at 30 fps, 11 % when both DUs share
  one display (one capture, one encode). Done without the sim: `tools/test_pattern.py --title ...` runs several
  moving test windows, and a bench configuration points two displays at them (windows must stay on-screen: a
  window parked off-screen is only composed by Windows when it is a 3D swap chain like the sim's pop-outs).

- **.NET DMC, layer 1** (`dotnet/`): protocol and resyncing message reader, a native WinUSB transport (no libusb),
  the DU connection (newest-frame flow, Identify, ping, brightness, health, firmware update) with tests against an
  in-process DU, and a bench tool. Verified on both DUs: 30.0 fps to each at 3.4 % of one core, ceiling 41-43 fps,
  firmware update in 3.5 s. The Python DMC stays the one in use until the port is complete.

### Fixed
- The "slow source" advice is only given for windows of the simulator; a test pattern or another program's window
  may be slow without the DMC blaming the sim.
- A DU's dropped-frame count from before the DMC connected was reported as new drops ("stall") at connect.

## [0.5.0] - 2026-09-20

Found by flying: advice with troubleshooting steps, the 13 fps pop-out problem, and a DU freeze on large frames.

### Fixed
- **DU froze for 2-3 s whenever a frame was larger than 64 KiB** (detailed pictures in flight; about once every
  three minutes on the PFD). TinyUSB casts a read size to 16 bits, so the DU's request for exactly 65536 remaining
  bytes read nothing; the DU spun for 3 s (starving the idle task: task watchdog warnings), dropped the frame and
  resynchronised. Reads are now capped at 16 KiB and never spin. Found with the DMC's stall log plus the DU's serial
  log during a flight on 20 Sept 2026. **[DU firmware]** Update the DUs from the Display units tab.
  Verified on hardware after the update over USB: twenty 217 KB frames (quality 100) received and shown, none
  dropped, no warnings in the DU's log.

### Added
- `tools/du_multi_test.py`: runs all connected DUs at once (each on its own display, all on the same display,
  assignments rotated) and records per-DU rates and times and the DMC's CPU.
- Protocol freeze for the hardware track (docs/usb-protocol.md 6a): the backlight reuses `SET_BRIGHTNESS`, scaler
  power gets `SET_PANEL_POWER` (0x08), INFO reports the capabilities. Documentation only; nothing sends them yet.
- Backlog: plan and order for the .NET DMC.
- `tools/load_test.py`: DMC cost with 0..N displays in use at once, without DUs (one MJPEG reader per display).
  Measured 19 Sept 2026, Fenix parked at night, six pop-outs, i7-8700K: 9 % of one core with one display in use,
  about 4-5 % more per display that is actually changing (upper ECAM at 12-16 fps), 20-23 % with five in use,
  290 MB. Displays that do not change cost almost nothing. Python is not the bottleneck for six DUs.
  Takeoff and climb with all six in use (`--all 15 --dwell 20`): 31-35 % of one core (one 46 % sample), 3.8-5 MB/s,
  290 MB. All six windows delivered 12.9 frames/s in lockstep, also with the capture cap at 60, so that rate comes
  from the sim / aircraft with six pop-outs open, not from the DMC (four pop-outs gave about 20 earlier).

- `tools/sim_fps.py`: the sim's real frame rate (SimConnect "Frame" event) next to a pop-out's frame rate and the
  window in focus. Found with it on 19 Sept 2026: AMD Fluid Motion Frames (HYPR-RX preset) held all pop-outs at
  13 fps whenever the sim had focus; every earlier measurement had been taken with another window in front. With
  the preset on Default: 27 fps per pop-out in focus, 19-20 fps of changing picture on the DU.
- Endurance, 19-20 Sept 2026: 22 hours with the sim, six pop-outs captured and one DU connected: no stalls, no
  reconnects, DMC memory flat at about 280 MB.
- **Advice on the status page**: when the DMC notices a problem it shows a panel with what it saw and numbered
  steps to fix it, linked to the right tab (`advice` in `/status`, `glasslink/advisor.py`; the .NET tray will use
  the same list). Rules: the sim delivers too few frames to a display in use ("slow source", under 16 a second for
  8 s), a DU not keeping up with its display, a DU with older firmware, a DU with no display assigned, pop-out
  windows not made by GlassLink, a display the automatic pop-out gave up on, displays without a click point in this
  aircraft, no brightness profile for the aircraft. The slow-source steps follow the PC: they name AMD or NVIDIA,
  say so when AMD Fluid Motion Frames or a driver frame limit is actually switched on (read from the driver's
  settings), and quote the sim's glass cockpit refresh rate (read from the sim's UserCfg.opt). Nothing is changed,
  only read. `docs/TROUBLESHOOTING.md` has the long form; `tools/content_fps.py` shows presented against changed
  frames.
- Capture cap raised from 24 to 40 fps (`capture.fps`): as a minimum interval, 24 let only every second frame
  through for sim rates between 24 and 48 fps. Unchanged frames are dropped before encoding.

### Changed
- The status page follows the system: light or dark with the Windows / browser setting, all sizes in `rem` so the
  text size setting and zoom scale the whole page, high-contrast mode and reduced motion respected, visible keyboard
  focus. State pills carry a symbol as well as a colour (check mark, exclamation mark, cross) and the colours meet
  WCAG AA in both themes. On narrow windows or at high zoom the tables turn into labelled cards, and nothing
  scrolls sideways (checked at 375 px). These are also the UI rules for the .NET DMC (docs/BACKLOG.md).

- The top bar shows the chain from sim to panel instead of "USB scanning" and the brightness link: **Sim** (aircraft,
  not in cockpit, not running), **Displays** found / configured (with "popping out" or "gave up"), **DUs** connected /
  expected (naming a disconnected DU, a running update, outdated firmware). Each is a link to its tab; the details
  that left the bar are on the System tab. The .NET tray icon will use the same three states.

### Fixed
- The keyboard focus could end up on a parked pop-out: switching back to the sim activates the sim's most recently
  active window, which was the last pop-out, off-screen. Key presses then went nowhere and frame generation stayed
  off. Pop-out windows are now marked never-activated (WS_EX_NOACTIVATE; `no_activate: false` per display turns it
  off), and the pop-out procedure always ends with the sim's main window in front.
- A DU froze when the settings of the display it was showing were changed: the restarted capture worker began a
  new frame count at 1 and the DU waited for a frame newer than the thousands it already had.
- The "pop-out windows not made by GlassLink" banner showed with a count of 0.

## [0.4.0] - 2026-09-18

Displays as data: six displays for a full cockpit, managed from the status page. No firmware change.
Verified with the Fenix on 18 Sept 2026: cold start pops out all six displays unattended, the FO side from the
copilot seat view, and the camera returns to the user's own custom view.

### Added
- **Display editor on the status page** ("Manage displays"): add, change and remove displays while the DMC runs.
  A new display gets 768 x 768 and the next free off-screen parking slot; size, parking position and capture rate
  are editable; removing a display unassigns any DU that showed it. API: `GET/POST /displays`,
  `POST/DELETE /displays/<name>`. (`glasslink/registry.py`)
- **Learn a pop-out click point by doing it once**: press Learn, the DMC puts the cockpit camera into the profile's
  standard view, you Right-Alt + click the display, and the DMC records where the click went (while the keys were
  down, so moving the mouse afterwards does not matter), stores it in the aircraft profile, names, sizes and parks the
  new window and restores the camera. Works for an aircraft without a profile too: the first learned display creates
  one. Auto pop-out pauses meanwhile and no longer moves the camera for displays that have no click point yet.
  (`glasslink/learn.py`, `POST /displays/<name>/learn`, `POST /learn/cancel`.)
  Verified with the Fenix on 18 Sept 2026 (add display, Learn, window adopted and parked).
- **FO side displays**: `fo_pfd` and `fo_nd` are in the built-in Fenix profile and pop out by themselves. They are
  out of reach from the left seat, so the DMC selects the sim's copilot seat view over SimConnect
  (`CAMERA VIEW TYPE AND INDEX` 1/4), clicks, and returns to the seat view and zoom it found. For other aircraft
  the Setup tab has **Learn (captain seat)** and **Learn (FO seat)**; the point is stored with its view.
  No key presses are involved. An earlier attempt in this cycle stored the user's own view as a sim custom camera
  (Ctrl+Alt+8 / Alt+8); measured on 18 Sept 2026, the sim ignores those injected keys (also with hardware scan
  codes), so that was removed, together with the Ctrl+Alt+9 / Alt+9 "restore my view" that never did anything.
  `SimConnect_CameraSetRelative6DOF` was tried too: it moves an outside camera without the cockpit.
  `popout.camera_restore: <number>` still loads that custom camera after a pop-out, for setups where the key works.
- **Back to your own view after a pop-out**: `popout.camera_restore_key` (Setup tab, "Camera after a pop-out") is
  the key combination that loads your flying view, exactly as bound in the sim, e.g. `shift+f1` for custom camera 1
  in the MSFS 2024 keyboard profile (Alt+number is the 2020 binding, which is why the earlier attempts did nothing).
  For cockpits whose flying view has no instruments on the monitor. The field records the keys you press.
- The automatic pop-out looks at the sim at most every 20 s while it has nothing to do (it opened a new SimConnect
  connection every 5 s while a display had no click point).
- Learning again closes the display's previous pop-out window first, so no duplicate is left behind.
- The automatic pop-out waits until the cockpit camera has really stopped moving (it compares grabs of the sim)
  instead of a fixed 2 s, and if the displays are lit and the PFD is not where the profile expects it, it waits and
  looks again, and does not click at all if they still disagree. Found on 18 Sept 2026: right after loading, the
  camera was still gliding, the clicks landed one instrument to the side (PFD -> ND, ND -> standby horizon) and the
  dark displays gave the DMC nothing to notice it by.
- The brightness link stands down when the aircraft dims its own pop-outs. For the Fenix that is the EFB setting
  "Home Cockpit Mode", read from `persistancy.xml` (checked every 5 s). The status page then shows "dimmed by the
  aircraft" and the DUs use their trim slider only. Described per aircraft in the profile (`popout_dimming`).

- **Close window** button per display (enabled only while the display has a window; Setup tab, `POST /displays/<name>/close`). The pop-outs are parked off-screen
  where you cannot reach them; the DMC closes the window for you, and the automatic pop-out opens it again if the
  display has a click point. Handy for testing the automatic pop-out and for getting rid of a wrong window.

- Pop-out windows that GlassLink did not make (popped out by hand, often parked off-screen where they go unnoticed)
  are listed on the Setup tab with a button to close them, and Learn mentions them: a display that is already
  popped out cannot be popped out again, so the click opens nothing. (`strays` in `/status`,
  `POST /popouts/close-strays`.)
- The automatic pop-out gives up on a display after two attempts that opened no window (`popout.max_attempts`)
  instead of moving the camera every minute for ever. Learning the display again or pressing Close window
  gives it a new chance.

### Changed
- The status page is split into four tabs: **Displays** (live pictures), **Display units**, **Setup** (the display
  editor and Learn) and **System** (version, process priority and CPUs, auto pop-out, brightness link, firmware
  image, links). The tab is part of the address (`/#dus`) and remembered. The learn banner shows on every tab.

### Fixed
- The status page no longer jumps every two seconds. The display cards were rebuilt on each refresh, so the page
  height collapsed while the five pictures reloaded. Cards are now built once and patched; a new picture is loaded
  in the background and swapped in only when the display's frame number has changed.

## [0.3.0] - 2026-09-18

Firmware updates without a serial cable.

### Added
- **Firmware update over USB from the status page.** Each DU row has an Update button that installs the built image
  (`firmware/build/glasslink_du.bin`, or `firmware.image` in the config) over the normal USB link, with progress and
  error text. The image is validated first (ESP application descriptor, project name, size). Stop-and-wait transfer
  in 32 KiB chunks, CRC32 and size check on the DU, 15 s timeout with abort, "UPDATING FIRMWARE" banner, rollback
  by the bootloader if the new image does not come up. New message `OTA_PROGRESS` (0x91). **[DU firmware]**
  DUs older than 0.3.0 need one last flash over the serial port.
  Verified on hardware: 445 KB installed in about 4 s, DU back on the new build 8 s after the click, flash slots
  alternating; an update abandoned halfway times out on the DU after 15 s (code 6) and a corrupted image is refused
  on its checksum (code 4), in both cases without a restart and with the running firmware untouched.
- The DU reports the flash slot it runs from (`slot` in INFO, shown on the status page), and the update status ends
  with what the DU actually came back with.
- `docs/panel-DBC088HXN60L050A.md`: the panel specification (an XGA panel cut to a square, pinout, rails, timing,
  backlight) and the hardware track towards an integrated DU board in the backlog.

### Fixed
- The firmware's build id followed the last CMake configure, not the last commit; it is now refreshed whenever the
  commit or the staged state changes.

### Changed
- Docs: measured behaviour of pop-outs under the brightness knobs (on/off only, never dimmed; Fenix "Home Cockpit
  Mode" is what dims them) and notes on manual pop-outs for the display editor.

## [0.2.2] - 2026-09-18

Dimming in hardware on the DU. Found by cycling a DU through all displays with the sim running.

### Fixed
- A dimmed DU was capped at 9 fps: software dimming rewrote 1.7 MB of external RAM per frame (85 ms). The ESP32-P4's
  pixel accelerator now blends each frame over black at the brightness ratio and writes it straight into the
  display's back buffer, which the panel driver then flips to: 27 ms per frame instead of 98, no copy, no tearing.
  The CPU lookup table remains as a fallback. **[DU firmware]**

### Added
- `tools/du_cycle_test.py`: assigns a DU to each display in turn and records DMC CPU, source and capture rate, and
  the DU's fps, decode, draw and transfer times. Measured with the sim and four pop-outs: DMC 10-13% of one core
  with a display in use, 6-8% with nothing assigned (0.2.0: 27%).

## [0.2.1] - 2026-09-18

Lower CPU cost and no competition with the simulator. No firmware change.

### Added
- The DMC runs at below-normal priority and pins itself to the last logical CPUs (`process` in the config; "auto"
  = the last third on machines with 8 or more, e.g. CPUs 8-11 of 12), so it never takes a core from the sim.
- Capture on demand: a display runs at full rate only while a DU is assigned or a viewer is connected. An unused
  display drops to `capture.idle_fps` (1) after `capture.idle_after_s` (5) and comes back within a second. The
  status page shows idle displays. Measured on the test pattern: about 50% -> 9% of one core when unused.
- `FIRMWARE_VERSION`: DUs are marked outdated only against the release in which the firmware last changed.

### Changed
- Default capture rate 30 -> 24 fps (real DUs and the Fenix run at 20-25 Hz); per-display `fps` still overrides.

### Fixed
- The DU's INFO answer could be lost behind the tail of a stale message at connect, leaving the firmware version
  blank on the status page; the DMC now asks again until it gets one.

## [0.2.0] - 2026-09-18

First version under the name GlassLink and the first one in git. Everything since the panel bring-up.

### Added
- Project renamed from ExtDisplay to **GlassLink**; the server is the DMC, a module with its panel is a DU.
  Python package `glasslink`, firmware project `glasslink_du`, USB strings "GlassLink" / "GlassLink DU",
  pop-out windows titled `GlassLink:<display>`. Protocol magic, USB IDs and interface GUID unchanged. **[DU firmware]**
- Brightness follows the cockpit knobs: L:vars read through SimConnect (no WASM module or FSUIPC needed; FSUIPC's
  WASM interface remains as an optional source), per-DU variable names in the aircraft profile, the DU's slider
  acts as a trim. DUs redraw at once on a brightness change and no longer write it to flash. **[DU firmware]**
- Identify as a real on/off toggle reported by the DU itself, shown as a banner with the DU's label over the
  live picture. **[DU firmware]**
- `SET_ASSIGNED` message: an unassigned DU shows its NOT ASSIGNED screen instead of the last frame. **[DU firmware]**
- Ping with round-trip time, Forget for disconnected DUs; unconfigured DUs disappear from the list when unplugged.
- Per-DU health log (stall and recovery lines with numbers) and a rolling history on the status endpoint.
- `POST /shutdown` and `tools/stop_server.py`: stop the DMC without killing a process that holds capture sessions.
- Versioning: `VERSION` file, this changelog, build id in DMC and DU, outdated-firmware marker on the status page.

### Fixed
- DU stream fell to under 1 fps after a DMC restart: the DU's resync could never find a message boundary while
  frames kept arriving. Now a byte-wise header resync on the DU **[DU firmware]** and the same on the host reader.
- DU received 4-10 fps from a 20 fps source: the host waited on an event that stayed set after a send. Now a
  condition with a predicate.
- Red and blue swapped on the panel (frame buffer byte order is B,G,R). **[DU firmware]**
- Reassigning a DU to another display did nothing until that display's frame counter caught up.
- LT8912B test-pattern generator left enabled by a diagnostic build masked the picture across reboots; cleared at
  every boot. Frame copies now wait for the DMA transfer to finish. **[DU firmware]**
- Status page: buttons and dropdowns no longer fight the 2 s refresh.

### Changed
- Change detection compares the capture buffer in place, samples rows first, copies only changed frames and
  encodes BGRX directly: DMC CPU 37% -> 27% of one core with four displays.
- Test pattern carries red and blue reference blocks so a channel swap cannot hide.

## [0.1.0] - 2026-09-14

The proof of concept and the first DU on the bench (not tagged; predates the git repository).

### Added
- Window capture of MSFS pop-outs (Windows.Graphics.Capture, PrintWindow fallback), off-screen parking, client-area
  crop, change detection, JPEG streaming over WebSocket and MJPEG, browser viewer and Raspberry Pi viewer.
- Automatic pop-out through SimConnect camera control and Right-Alt clicks, per-aircraft profiles with fixed click
  points, optional PFD detection, import of Pop Out Panel Manager profiles, camera restore.
- USB DU protocol (16-byte header, latest-frame-only flow control, WinUSB through MS OS 2.0 descriptors) and the
  host side: hot-plug, assignment by serial, status page.
- DU firmware for ESP32-P4: LT8912B HDMI bridge at a custom 768x768 timing, hardware JPEG decode, DMA2D frame copy,
  idle screens, serial in NVS, OTA partitions.
