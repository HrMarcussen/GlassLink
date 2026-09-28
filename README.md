# GlassLink

GlassLink drives real glass-cockpit display units from a flight simulator over USB. A DMC service on the sim
PC captures the aircraft's displays (PFD, ND, ECAM, ...) and streams them to small embedded DU modules, each
driving its own LCD. No GPU ports, no extra PCs: one USB cable per display. Built around an Airbus-style home
cockpit (MSFS 2024, Fenix A320), usable with any aircraft through per-aircraft profiles.

Terms used in this project, borrowed from the aircraft:

| Term | In the aircraft | Here |
|---|---|---|
| **DMC** | Display Management Computer, feeds the display units | the GlassLink server on the sim PC: `GlassLink.exe`, a .NET tray app (`dotnet/`); the Python original (`glasslink/`) remains the reference and test tool |
| **DU** | Display Unit, one screen in the panel | one GlassLink module: ESP32-P4 board + HDMI bridge + LCD (`firmware/`) |

The DMC pops the displays out of the sim automatically, parks the windows off-screen, captures them, and sends
each DU the newest frame of the display it is assigned to (JPEG over a WinUSB vendor interface, decoded in
hardware on the DU). Brightness follows the cockpit knobs through SimConnect. A browser or a Raspberry Pi can
view the same streams over the LAN (WebSocket/MJPEG), which is how the project started and is still handy for
testing. Protocol: `docs/usb-protocol.md`. Open work: `docs/BACKLOG.md`.

The sections below are the engineering log, oldest first: the original LAN proof of concept, then the USB DUs.

```
 Sim PC (Windows)                                        Viewer PC today / Raspberry Pi 5 later
 ┌─────────────────────────────────────┐                 ┌──────────────────────────────┐
 │ MSFS pop-out windows (PFD, ND, …)   │  Windows.       │ Browser (Chromium kiosk)     │
 │ or FenixDisplay.exe windows         │  Graphics.      │   http://simpc:8765/view/pfd │
 │            │                        │  Capture        │                              │
 │ GlassLink DMC (GlassLink.exe)       │  → JPEG → WS    │ or native viewer (pygame)    │
 │  capture → crop → JPEG → WebSocket  │─────LAN/USB────▶│   pi/viewer.py               │
 └─────────────────────────────────────┘                 └──────────────────────────────┘
```

Why pixel streaming: re-rendering PFD/ND/ECAM from LVARs would mean re-implementing the aircraft's display
software. Capturing the rendered windows gives an exact copy for any aircraft. LVARs remain available
(FSUIPC WebSocket Server, Fenix GraphQL gateway) for future text-only displays such as a DCDU.

## Requirements (sim PC)

- Windows 10 1903+ / Windows 11 (Windows.Graphics.Capture).
- The DMC: either the setup program from a release (`GlassLink-<version>-setup.exe`, nothing else to install), or
  a checkout with the .NET 10 SDK (`start-server.bat` builds and starts it). See `dotnet/README.md`.
- The Python reference DMC and the tools (`tools/*.py`, `firmware/`): Python 3.10+ and a venv with
  `pip install -r requirements.txt`. Not needed to run a release.

```
python -m venv .venv
.venv\Scripts\pip install -r requirements.txt
```

## Quick start

1. Start the DMC: double-click `start-server.bat` (a checkout) or the Start menu entry (an installed release). It
   sits in the notification area; its menu opens the status page, and offers "Start with Windows" and "Start and
   stop with the simulator". The Python reference DMC is `start-dmc-python.bat`; only one DMC runs at a time.
2. Start the sim and load the aircraft. Once you are in the cockpit, the server notices that the configured
   displays have no window, resets the cockpit camera through SimConnect, zooms out, pops out PFD / ND /
   upper ECAM / lower ECAM with Right-Alt + click, renames them, resizes them to `client_size`, parks them
   off-screen (`position`) and restores your camera zoom. Takes about 15 s; do not touch mouse/keyboard meanwhile.
   You can also trigger it by hand: `popout-displays.bat` (= `tools/auto_popout.py`).
3. On any PC/Pi in the LAN open `http://<sim-pc-ip>:8765/view/pfd`. The page is black with the picture
   letterboxed to the browser window; click it for fullscreen. On a 768x768 panel in a kiosk browser the
   768x768 pop-out fills the panel exactly. Only `?fps=1` shows the green debug overlay (never on by default).
   Other options: `?rotate=90`, `?mode=mjpeg`.
   Or run the native viewer: `python pi/viewer.py --url ws://<sim-pc-ip>:8765/ws/pfd`.

Pop-out click positions come from a per-aircraft **profile** (built-in for the Fenix; add others under
`popout.profiles` in `config.json`). A profile is the camera zoom plus, for each display, its position as a
fraction of the sim window after the camera reset, so it works with a dark cockpit (cold and dark start) and at
any resolution with the same aspect ratio. When the displays are powered, the PFD detection refines the points.
The profile is chosen by a substring of the sim's aircraft title. To calibrate a new aircraft:

```
.venv\Scripts\python toolsuto_popout.py --calibrate            # resets camera + zoom, writes calibrate.png with markers
.venv\Scripts\python toolsuto_popout.py --save-profile "PMDG 737" --zoom 30 --points pfd=X,Y nd=X,Y ...
```

If you already have MSFS Pop Out Panel Manager profiles, import them instead of calibrating: `python
tools\import_popm.py` lists them, `--import "<name>" --as <aircraft title substring>` converts one (dot positions,
camera view per panel, zoom) into an GlassLink profile. Profiles can use the sim's own camera presets per display
(`camera: {"type": 1, "index": 1}` = Cockpit Pilot view, `{"type": 2, "index": n}` = the aircraft's Instrument view
n+1) instead of the camera reset; the server switches views through SimConnect while popping out.

Auto pop-out is controlled by the `popout` section of `config.json` (`auto`, `aircraft` substring of the sim's
aircraft title, `zoom`, `grace_s`, `retry_s`, `camera_restore`, `camera_slot`) or `serve --auto-popout /
--no-auto-popout`. `camera_restore: "current"` saves the view you had into custom camera `camera_slot`
(Ctrl+Alt+9 by default) before the reset and loads it again (Alt+9) when done, so the view snaps back; set it to
a digit to load one of your own custom cameras instead, or to `null` to just reset and restore the zoom. It only acts when the
sim is in the cockpit view with a matching aircraft and a display has been missing for `grace_s` seconds, so it
also recovers after a flight reload. Manual alternative for any window: `python -m glasslink assign <name>
[--title T | --hwnd H] [--size WxH] [--position X,Y]` (see `list-windows`).

Stopping the server: press Ctrl+C in its window or close the window. Both stop the capture sessions cleanly first.
Do not kill the process from Task Manager while the sim runs: terminating a process that holds Windows Graphics
Capture sessions on the sim's DirectX 12 windows has been seen to trigger AMD GPU driver timeouts on this machine
(two "video engine timeout" reports on 8 Sept 2026, both within a minute of a hard kill of the server). If timeouts
persist, run with `--backend printwindow`, which uses no capture sessions.

Windows Firewall must allow inbound TCP 8765 for the venv's `python.exe` (Windows usually asks the first time;
otherwise, from an admin PowerShell):

```
New-NetFirewallRule -DisplayName "GlassLink 8765" -Direction Inbound -Protocol TCP -LocalPort 8765 -Action Allow
```

## CLI

| Command | Purpose |
|---|---|
| `serve [--backend auto\|wgc\|printwindow] [--port N] [--auto-popout\|--no-auto-popout] [--fake-modules N]` | run the server; `--fake-modules N` serves N in-process fake USB modules for UI work without hardware |
| `list-windows [--all] [--process X.exe] [--json]` | visible top-level windows with physical-pixel client sizes |
| `dump-windows --process X.exe` | all windows of a process incl. hidden ones (JSON lines) |
| `show-window --hwnd 0x…` | `ShowWindow(SW_SHOWNOACTIVATE)` on a hidden window |
| `assign <name> [--title T] [--hwnd H] [--process P] [--size WxH] [--position X,Y] [--no-rename]` | map a window to a display, save config |
| `snapshot <name> [--backend B] [--out DIR]` | grab one frame, write raw and cropped PNG (checks the crop geometry) |

## Tools

| Script | Purpose |
|---|---|
| `tools/auto_popout.py [--only a,b] [--profile P] [--no-detect] [--no-camera] [--points name=x,y …] [--calibrate] [--save-profile NAME --zoom Z --points …] [--grab]` | manual trigger of the pop-out procedure (profile points after camera reset + zoom, optional PFD detection, Right-Alt clicks, rename/resize/park) and profile calibration for new aircraft. |
| `tools/import_popm.py [--import NAME --as KEY] [--screen WxH]` | convert MSFS Pop Out Panel Manager profiles (dots, cameras, zoom) into GlassLink profiles |
| `tools/test_pattern.py` | synthetic moving window with a machine-readable clock strip, for testing without the sim |
| `tools/measure_latency.py --url ws://…/ws/pattern` | decodes the clock strip from received frames and prints fps, bandwidth and draw-to-decode latency |

## HTTP / WebSocket API

| Endpoint | Description |
|---|---|
| `GET /` | status page: display cards with live snapshot, window state, fps and counters; USB module table with display assignment, label, brightness, Identify/Ping buttons. Refreshes every 2 s. |
| `GET /status` | JSON: per display seq, size, fps (changed frames published per second), last frame age, window, backend, error, clients, and `counters` (`received` = frames delivered by the capture = the sim's present rate for that window, `throttled`, `unchanged`, `published`) |
| `GET /view/<name>` | browser viewer |
| `GET /ws/<name>` | WebSocket. Server sends a JSON hello, then binary frames `uint32 seq, uint32 server_ms, JPEG…`. The client sends the text `n` after drawing each frame; the server then sends the *newest* frame once one newer than the last sent exists (latest-only, no queue build-up on slow clients). |
| `GET /stream/<name>` | MJPEG (`multipart/x-mixed-replace`) for `<img>`, VLC, ffplay |
| `GET /snapshot/<name>.jpg` | last frame |
| `GET /modules` | USB display modules: serial, assigned display, alive, firmware info, stats, frames sent |
| `POST /modules/<serial>` | JSON body with any of `display` (name or `""` to unassign), `brightness` 0-100, `rotation` 0/90/180/270, `label`, `command` (`ident`, `ping`, `info`, `reboot`) with optional `arg` |

## Configuration (`config.json`, see `config.example.json`)

- `server.host/port`
- `capture.backend` (`auto` = wgc with printwindow fallback), `fps`, `quality` (JPEG), `subsampling` (`420`/`444`)
- `displays.<name>.match`: `process`, `class`, `title` (substring), `title_exact`, `title_regex`, `client_size`, `hwnd`
- `displays.<name>.client_size` / `position`: applied to the window when found (sim renders at that size)
- `displays.<name>.crop` `[x,y,w,h]` override, `max_size` downscale, per-display `fps` / `quality`
- `usb.enabled` (default true) and `usb.scan_interval_s`: hot-plug scan of GlassLink USB modules
- `modules.<serial>`: `display`, `brightness`, `rotation`, `label` per module serial (written by `POST /modules/<serial>`)

## How it works

- `glasslink/windows.py` makes the process per-monitor DPI aware, enumerates windows, renames/resizes them and
  computes the client-area crop. WGC delivers the DWM extended-frame rect, PrintWindow the GetWindowRect rect;
  the crop picks whichever matches the frame size.
- `glasslink/capture/wgc.py` uses the `windows-capture` package (Windows.Graphics.Capture by HWND, cursor and
  border off, minimum update interval = 1/fps). Frames only arrive when the window presents.
- `glasslink/capture/printwindow.py` polls `PrintWindow(PW_RENDERFULLCONTENT)` at the configured fps.
- `glasslink/display.py` (one worker per display) finds the window, applies geometry, throttles, crops, skips
  unchanged frames, JPEG-encodes on the capture thread (libjpeg-turbo via `simplejpeg`, releases the GIL) and
  hands the frame to the asyncio hub. Lost windows are re-detected every 2 s (pop-outs are recreated on reload).
- `glasslink/server.py` (aiohttp) serves the viewer and the endpoints above.
- `glasslink/modules.py` implements the host side of `docs/usb-protocol.md`: pyusb/libusb transport to the
  WinUSB-bound module, one worker thread per plugged-in module (READY -> newest JPEG of the assigned display,
  INFO/STATS/LOG bookkeeping), a hot-plug manager (scan every 2 s, assignment by serial, commands). Tested
  against a fake transport in `tests/test_modules.py` (`python -m unittest discover -s tests`).

## Findings so far (without the sim, synthetic test window `tools/test_pattern.py`)

Test PC: Windows 11 26200, i7-8700K, RX 7900 XTX, 2560x1440 at 175 % DPI, Store Python 3.13.

| Check | Result |
|---|---|
| `windows-capture` 2.0.1 on Python 3.13 | installs (abi3 wheel), captures by HWND |
| DPI / crop geometry | `list-windows` reports physical pixels; WGC frame = extended frame bounds, PrintWindow frame = window rect; both crops verified pixel-exact with `snapshot` |
| Throughput | 1050x1050 window at 30 fps source, fps limit 20: ~17.5 fps captured, ~47 KB/frame, ~6.9 Mbit/s to the browser viewer, seq gaps 0 |
| Window behind other windows (occluded) | captured normally |
| Window fully off-screen | frames stop for the pygame test window (SDL stops presenting). MSFS pop-outs behave differently and keep rendering off-screen, see the sim findings. |
| Window minimised | frames stop (expected) |
| Static window | 0 fps, no errors, last frame cached and sent to new clients |
| Browser viewer / native viewer | both display the stream, reconnect after server restart |
| MJPEG endpoint | works |

## Test plan with the sim (MSFS 2024 + Fenix)

1. `list-windows --process FlightSimulator2024.exe` and `dump-windows --process FenixDisplay.exe`
   (does Fenix's display renderer expose per-display windows or a tray menu with "Displays"?).
2. `assign` the four pop-outs, `snapshot pfd` to check the crop, `serve`.
3. View from another PC; check `/status` fps; occlusion test with the sim in front; measure sim FPS with 0 vs 4
   pop-outs and glass-to-glass latency (phone video of sim + viewer).
4. Repeat with FSLabs once.

## Findings with the sim (MSFS 2024 1.8.16 + Fenix A320 2.4, cold-and-dark on the runway)

| Check | Result |
|---|---|
| FenixDisplay.exe standalone windows | Not available: the ProSim-derived tray menu ("Displays", "Display Setup mode", "Fullscreen") is compiled in but the main form and menu are hidden, render nothing, and no tray icon is registered for FenixDisplay.exe. MSFS pop-outs are the capture source. |
| Pop-out creation | Right-Alt + click is the only way. Automated in `glasslink/popout.py`: SimConnect `CAMERA REQUEST ACTION = 1` resets the cockpit camera and `COCKPIT CAMERA ZOOM = 30` brings all four DUs into view (pitch/yaw are not writable on MSFS 2024); the PFD is located by its blue attitude sphere in a `PrintWindow` grab and the other DUs by fixed offsets, so it is independent of the user's camera and resolution. Pop-outs are AceApp windows owned by the main sim window and share its title until renamed. |
| Assignment / rename / resize | `assign` renamed each pop-out to `GlassLink:<name>` and set the client area to 640x640; WGC frame 644x694, crop (2, 52, 640, 640) verified pixel-exact for all four displays. |
| Pop-outs behind the sim | Not possible: owned windows always stay above their owner, so an on-screen pop-out covers part of the cockpit view. |
| Pop-outs fully off-screen | **Works.** MSFS keeps rendering a pop-out moved outside the desktop (e.g. x = 2600 on a 2560-wide screen) and both WGC and PrintWindow keep delivering frames. The config now parks the four pop-outs at x 2600/3300, so no extra monitor or GPU port and no cockpit obstruction. |
| Backends | WGC (primary) and PrintWindow (fallback) both capture the DX12 pop-outs. |
| Automatic pop-out | Verified end to end: with all four pop-outs closed and the server started with `popout.auto`, it detected the Fenix in the cockpit, reset the camera, zoomed to 30, found the PFD at (1237, 1175), popped out and parked all four displays in 19 s and restored zoom 50. Works regardless of the camera the user had before (the reset discards any custom view; re-select it afterwards) and scales with resolution because the click points come from the detected PFD size. `position` values in config.json are absolute pixels (x >= screen width to park off-screen), so adjust them for another desktop resolution. |
| Frame sizes | 30-47 KB per 640x640 JPEG at quality 85; static displays publish nothing (fps 0), the upper ECAM on the ground updates at 6-8 fps because only the digits change. |
| Remote viewer | iPhone Safari over WiFi showed the live PFD at `http://192.168.1.10:8765/view/pfd`; ~870 kbit/s while the aircraft was parked (only changed frames are sent). |
| Frame-rate ceiling | The stream can never exceed the sim's own present rate: with the PC heavily loaded by this session the sim presented ~13 fps (4 pop-outs) and ~13 fps (0 pop-outs), i.e. **pop-outs cost no measurable sim FPS**. Normal sim rate on this PC is 40-50 fps; the capture cap is 30 fps. |
| End-to-end latency | `tools/measure_latency.py` against `tools/test_pattern.py` (1120x1120 window, 60 fps source, same PC, loaded): 26.7 fps, 54 KB/frame, 11.9 Mbit/s, latency min 26 / median 43 / p90 54 / max 71 ms from draw to decoded frame in the client (excludes the panel's scan-out and the viewer's paint). |
| CPU | Sim 42 % of 12 threads (5 cores), FenixDisplay 5 %, GlassLink server 1-6 %, i.e. the server is not the bottleneck. |

Session workflow: start the server once; every time you get into the Fenix cockpit it pops the displays out by itself.



## Raspberry Pi

See `pi/SETUP.md` (Chromium kiosk or `pi/viewer.py` + systemd unit, USB Ethernet gadget for a single cable).

## Can the viewer be an ESP32 instead of a Pi?

The server side does not care what the client is: the WebSocket protocol is pull-based (the client asks for
the next frame when it has drawn the previous one), frames are plain JPEG, and the sim renders the pop-out at
the panel's native size, so the client only has to decode and blit. What matters is the client's ability to
decode a 768x768 JPEG 20-30 times per second and drive the panel.

| Board | Verdict for a 768x768 panel |
|---|---|
| Classic ESP32 / ESP32-S3 | No. No hardware JPEG decoder (software decode of 768x768 takes ~150-300 ms), PSRAM bandwidth is too low for a 768x768 16-bit framebuffer at a usable refresh, and the RGB/SPI panel interfaces top out around 480x480 / 800x480. Fine for a small text display (DCDU/MCDU) rendered from LVARs, not for a PFD. |
| ESP32-P4 (2024/25, ~USD 15-30 for a module/dev board) | Yes, with caveats. Dual RISC-V 400 MHz, up to 32 MB PSRAM, **hardware JPEG decoder**, MIPI-DSI display interface, USB 2.0 high-speed (can act as a USB Ethernet gadget for a single-cable link), Ethernet MAC (needs a PHY), Wi-Fi only via an ESP32-C6 companion chip (~20-30 Mbit/s real). Expected 20-30 fps at 768x768 with the HW decoder. Firmware: ESP-IDF in C, `esp_websocket_client` + `esp_driver_jpeg` + `esp_lcd_mipi_dsi`, roughly 300-500 lines, a weekend of work. **Caveat:** the P4 has no HDMI, so an HDMI panel needs a MIPI-DSI-to-HDMI bridge board (Lontium LT9611/LT8912 based, ~USD 10-20) or you switch to a MIPI-DSI panel (square MIPI panels exist at 720x720 and 800x800, 768x768 is rare). |
| Cheap Linux SBC with HDMI (Raspberry Pi Zero 2 W ~USD 15, Radxa Zero 3W, Orange Pi Zero 2W, ~USD 15-25) | Yes, today, with the existing `pi/viewer.py`. Four A53 cores decode 768x768 JPEG in ~20-30 ms with libjpeg-turbo, HDMI out matches the existing panels, USB gadget Ethernet works, boot time 15-25 s. |

Recommendation: since the panels are HDMI and the viewer already exists, a Pi Zero 2 W (or similar SBC) is the
cheapest working option; the price gap to an ESP32-P4 plus HDMI bridge is a few dollars. Choose the ESP32-P4 if
you want an instant-on, no-OS unit with an integrated MIPI panel; the server needs no changes for it.

## ESP32-P4 display module: bill of materials (EU sources, Sept 2026)

Existing per display: Aptus Display DBC088HXN60L050A 8.82" 768x768 8-bit LVDS panel (500 nits) with its
HDMI/VGA-to-LVDS scaler board (AD768L60V01 / HDP802, 12 V DC input). Both stay; the module feeds HDMI into that board.

| Per display | Source | Approx. price |
|---|---|---|
| Waveshare ESP32-P4-NANO (32 MB PSRAM, HW JPEG decoder, 2-lane MIPI-DSI on RPi-style 15-pin FPC, 10/100 Ethernet, Wi-Fi 6, USB 2.0 HS Type-C) | BerryBase (DE), Botland (PL), Kiwi (NL) | EUR 22 |
| Olimex MIPI-HDMI adapter (Lontium LT8912B, DSI 15-pin in, HDMI out) + 15-pin FPC cable | olimex.com (BG) | EUR 15 + 2 |
| HDMI cable 0.3 m | any | EUR 4 |
| 802.3af/at PoE splitter with 12 V / 2 A output | Botland, Amazon.de, Reichelt | EUR 10 |
| 12 V -> 5 V 2 A buck module (Mini560 or LM2596 type) to power the ESP32 | any | EUR 2 |
| Cat 5e/6 patch cable | any | EUR 2 |

One-time: PoE switch with 4 (captain) or 8 (captain + FO) ports and >= 8 W per port budget, e.g. TP-Link
TL-SG1005P / TL-SG1008P, EUR 45-70. For the first prototype the existing 12 V supply and a USB-C cable to the PC
are enough; PoE comes when it goes into the cockpit.

Decisions (8 Sept 2026): modules connect over **USB only** (vendor-specific interface bound to WinUSB, no network
adapter), with a 12 V barrel jack passed through to the scaler board and a small buck for the ESP32. Modules ship
with blank firmware and identify themselves by a serial GUID; the cockpit display is assigned per module in the
app. Protocol: `docs/usb-protocol.md`. The Python server stays during bring-up; the product will be a .NET tray
application that is server, launcher and configuration UI in one.

Notes:
- The scaler board wants 12 V (about 5 W with the panel), so a single USB cable cannot power a display; PoE gives
  one cable per display for data and power and scales to the FO side with just more switch ports. USB-NCM stays
  possible as a data link with a separate 12 V supply.
- Alternative board: Olimex ESP32-P4-DevKit (EUR 16, Ethernet, optional PoE module for the ESP only) if the
  high-speed USB device port is not needed.
- The LT8912B passes the DSI timing through to HDMI, so the ESP32 is programmed for 768x768@60 with standard
  porches (~45 MHz pixel clock); the scaler board accepts it natively or rescales, to be verified on the first unit.
- Firmware: ESP-IDF, esp_websocket_client, esp_driver_jpeg (hardware), esp_lcd_mipi_dsi + esp_lcd_lt8912b
  component (Nicolai Electronics), lwIP over Ethernet or tusb_ncm over USB, a small config web page on the module.

## Module firmware

`firmware/` is the ESP-IDF project for the ESP32-P4 display module (see `firmware/README.md`): USB vendor device
with WinUSB descriptors, LT8912B HDMI output, hardware JPEG decode, idle/ident screens, OTA. Toolchain: ESP-IDF
v5.5.5 under `C:\Espressif` (Espressif Windows installer).

## Backlog

See `docs/BACKLOG.md` (naming, per-aircraft profiles for FSLabs/Boeing, firmware bring-up, .NET app).

## Not in the PoC

H.264 / hardware encoding, authentication, brightness control from the cockpit knobs (later: lvar → viewer
dimming), native rendering of DCDU/MCDU text from lvars (`tools/probe_lvars.py` shows the data path).

### Performance note (17 Sept 2026)
With the sim running, four 768x768 pop-outs captured and one USB module on the upper ECAM, the server costs about
27% of one core (was 37% before the change-detection rework: compare the 4-channel capture view in place, coarse
sample first, copy only changed frames, encode BGRX directly). Fenix redraws its displays about 20 times a second,
so 18-20 fps on a moving display is the source rate, not a limit of the pipeline (30 fps cap, tested at 30 with
the synthetic pattern). Cold-and-dark auto pop-out: four windows in 20 s from profile points, camera restored.

### Brightness follows the cockpit (17 Sept 2026)
The pop-out windows do not dim with the DU brightness knobs (measured: PFD at knob 5% captured as bright as the
ECAMs at 50%), so the server applies it. `glasslink/simvars.py` reads the L:vars straight through SimConnect:
MSFS 2024 accepts "L:NAME" in data definitions, so no WASM module, no FSUIPC and no extra process is involved
(FSUIPC7's WASM interface DLL remains available as `brightness.source = "fsuipc_wasm"`; FSUIPC's WebSocket server
is not needed and hung on WebSocket upgrades here). The pop-out profile carries the per-DU variable names
(Fenix: `N_DISPLAY_BRIGHTNESS_CO/CI/ECAM_U/ECAM_L/FO/FI`, 0..1, the effective value after the knob
`A_DISPLAY_BRIGHTNESS_*`), chosen by the aircraft title like the click points. Effective module brightness =
sim value x the module's trim slider, sent as SET_BRIGHTNESS whenever it changes (host polls at 10 Hz); the
module dims every frame through a lookup table, redraws its last frame at once on a change, and does not
persist the value.

### Pop-outs and the brightness knobs, measured (18 Sept 2026)
With the real cockpit knob and the PFD pop-out visible on the main monitor, three measurements were recorded twice
a second: the DMC's capture, a PrintWindow of the same window, and the screen pixels at that spot. From 100% down to
15% none of them changed; at 0% the DU switches off and the pop-out goes black, and it comes back at full brightness
after the Fenix's power-up delay. So in MSFS 2024 1.8.16 with Fenix 2.4 a pop-out is either on or off and never
dimmed, which is why the DMC applies brightness itself. Thomas's earlier monitor-behind-the-MIP setup did dim the
pop-outs gradually; pop-outs were also taskbar windows then, so the sim or Fenix has since changed how they are made.

**Resolved the same day:** the gradual dimming Thomas remembered is Fenix's EFB sim setting **Home Cockpit Mode**
("Used to allow pop-out displays to be dimmed"). Since the "Big Fenix Update" (21 Aug 2025) the display brightness is
applied on the 3D model ("art side"), so pop-outs stay at full brightness unless that mode is on. GlassLink expects
it **off**: the DMC then applies brightness to the DU (today by dimming pixels in hardware, later by dimming the
backlight, which keeps blacks black). With Home Cockpit Mode on, the pop-out picture is already dimmed and the
brightness link would dim a second time: either switch the mode off or set `brightness.enabled` to false.

## Troubleshooting
Jerky displays, focus problems, displays that will not pop out: see [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md).
