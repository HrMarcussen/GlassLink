# GlassLink DMC for .NET

The port of the Python DMC (`../glasslink`), layer by layer, with the Python version as the reference. Same USB
protocol (`../docs/usb-protocol.md`), same `config.json`, same HTTP API and status page. Plan and UI requirements:
`../docs/BACKLOG.md`, item 5.

| Layer | State |
|---|---|
| 1. USB and DUs: protocol, WinUSB transport, DU connection (frames, commands, health, firmware update) | done, verified on two DUs |
| 1b. DU manager: hot-plug, assignments, labels and trim from `config.json` (unknown keys survive a save), brightness = knob x trim | done, verified on two DUs |
| 2. Capture and encode: window finder (match rules, size, park, never-activate, client crop), Windows.Graphics.Capture, change detection, libjpeg-turbo, capture on demand | done, verified on the sim's six pop-outs |
| 3. SimConnect client (variables, L:vars, camera, sim fps), aircraft profiles, pop-out procedure, automatic pop-out with give-up, return to the user's view, brightness link with Home Cockpit Mode stand-down | done, verified on the sim: cold start pops out all six |
| 3c. Learn a click point (captain / FO seat), stray pop-outs | done (Learn not yet tried on the sim) |
| 4. The DMC program (`src/GlassLink.Dmc`, `GlassLink.exe`): HTTP API identical to the Python one, serving the same status page and viewer; display editor; advice; firmware update; tray icon with the three states; log file; below-normal priority and CPU pinning; single instance; graceful stop | done, verified against the sim and both DUs |

```
dotnet build GlassLink.slnx -c Release
src/GlassLink.Dmc/bin/Release/net10.0-windows10.0.26100.0/GlassLink.exe      # the DMC: tray icon, http://localhost:8765/
dotnet test GlassLink.slnx
dotnet run --project src/GlassLink.Bench -- list
dotnet run --project src/GlassLink.Bench -- stream --seconds 20 --fps 30
dotnet run --project src/GlassLink.Bench -- manage --seconds 20      # the DU manager with your real assignments
dotnet run -c Release --project src/GlassLink.Bench -- run --seconds 30   # a DMC in miniature: capture the configured windows, feed the DUs
dotnet run -c Release --project src/GlassLink.Bench -- sim                  # what SimConnect says: aircraft, camera, fps, knobs
dotnet run -c Release --project src/GlassLink.Bench -- run --sim --seconds 90   # with automatic pop-out and brightness (Python DMC stopped)
dotnet run --project src/GlassLink.Bench -- update ../firmware/build/glasslink_du.bin --serial ff69
```

The bench tool needs the DUs for itself: stop the Python DMC first (`python tools/stop_server.py`).

Design notes
- **WinUSB directly** (P/Invoke, no libusb): the DU binds to Windows' own driver through its MS OS 2.0 descriptors.
  One reader thread per DU keeps a read pending and never cancels it; a bulk read that is cancelled by a timeout can
  lose the bytes that arrived at that moment. Writes end with a zero-length packet when needed (pipe policy).
- **The DU is the clock**: one frame in flight, always the newest (`IFrameSource.WaitNewer` is a real predicate wait).
  While a READY is pending the connection waits for the next picture, not for the USB read, so a picture that
  appears after a still phase goes out at once (the Python version can sit in its 250 ms read first).
- Measured 21 Sept 2026, two DUs on one hub, 768x768 test pictures: 30.0 fps to each at 3.4 % of one core and
  52 MB; ceiling 41-43 fps per DU (decode 6 + draw 12.5 + transfer 3.5 ms); ping 0.4-0.9 ms; firmware update
  445 KB in 3.5 s.
- **Capture**: only the client area is copied from the GPU, and a frame nobody wants is refused before any copy
  (`WantFrame`), so an unused display costs almost nothing. All captures share one D3D11 device; the pixel handler
  runs under its lock and only compares and copies, the JPEG is encoded after the lock is released.
- **JPEG**: libjpeg-turbo through Quamotion.TurboJpegWrapper, 1.3 ms for a 768x768 PFD picture. SkiaSharp was tried
  first: 6.9 ms, its libjpeg-turbo is built without the assembler routines.
- Measured 21 Sept 2026, two 30 fps test windows to two DUs, Release build: 13-15 % of one core, 105 MB
  (Python DMC, same test: 19 %, 285 MB).
- Measured 21 Sept 2026 on the sim (Fenix parked, six pop-outs parked off-screen, DU1 on the PFD, DU2 on the ND):
  all six windows deliver 30 frames/s, the four unused displays are refused before the copy (29 of 30), pictures
  cropped cleanly to the client area with the right colours; 10.6 % of one core, 136 MB.
- **SimConnect** is bound natively (`lib/SimConnect.dll`, P/Invoke): no managed SDK wrapper, no WASM module. Variables
  are registered once and pushed by the sim on change; the connection thread waits for the sim, reconnects and
  re-registers. Replace the DLL with the one from the official MSFS SDK before publishing the repository.
- **A closed pop-out lingers** as a cloaked window that still has its title and counts as visible. Windows are
  therefore enumerated without cloaked ones, and a display has a window only while that window is alive (visible,
  not cloaked, still matching its rule). Found 21 Sept 2026 when displays were not re-popped after a close.
- Measured 21 Sept 2026, cold start on the sim: six displays popped out in about 35 s after the 10 s grace period
  (captain seat, then copilot seat), camera back on the user's view with Shift+F1, DUs fed, brightness 25 % from the
  knob applied on the DU. Steady state 10 % of one core, 138 MB.
- **One status page for both DMCs**: `GlassLink.exe` serves `glasslink/static/*.html` (copied at build time) and
  answers the same routes with the same JSON field names, so the page cannot tell which DMC it talks to (`engine`
  in `/status` says ".NET"). `python tools/stop_server.py` stops either one.
- **Numbers in the configuration tree** are read with `JsonNode.AsDouble()` only: `GetValue<double>()` throws for a
  number that this program put into the tree itself (as an int), and works for one parsed from the file.
- **Tray**: a disc with a tick, an exclamation mark or a cross (never colour alone), the three status lines as
  tooltip and menu, the status page in a window of its own (WebView2, `StatusWindow.cs`: no Windows title bar, the
  page's bar is the title bar), "Start with Windows" and "Start and stop with the simulator" as menu items that only
  the user switches. No console: the log goes to `logs/dmc-<date>.log`.
- Ported 21 Sept 2026: the PFD-sphere safety check (the pop-out refuses to click while a lit PFD is not where the
  profile expects it; verified live: "PFD seen at (1238, 1176), as the profile expects") and DU rotation.
- Ported 22 Sept 2026: `max_size` (area-averaging downscale before the encode, `Downscale.cs`), Learn verified on
  the sim (copilot view, click detected, point stored, window parked, camera back), "Start and stop with the
  simulator" (an entry in the sim's exe.xml written only on the user's click in the tray, `--with-sim` makes the DMC
  quit when the sim's process has gone), and the release build.

## Release build and installer

`tools\build-release.ps1` publishes one self-contained `GlassLink.exe` (no .NET runtime to install, about 170 MB
unpacked, 66 MB zipped) with the status page, `config.example.json`, the DU firmware image from `firmware\build`
and a `BUILD` file with the git describe, into `dist\GlassLink-<version>\` and `dist\GlassLink-<version>-win-x64.zip`.
With Inno Setup 6 installed (`winget install JRSoftware.InnoSetup`) it also builds `dist\GlassLink-<version>-setup.exe`
from `installer\GlassLink.iss`: Program Files, a Start menu entry, optional "start when I sign in" and an optional
firewall rule for the status page from other devices; a running DMC is stopped gracefully before its files are
replaced (`GlassLink.exe --quit` asks the running one to stop and waits until it has gone). The script asks the
running DMC to stop as well, because its files are in use otherwise.

An installed copy finds no `config.json` above itself and uses `%LOCALAPPDATA%\GlassLink\config.json` (created from
`config.example.json` on the first start; the logs go next to it), and takes the firmware image from `firmware\`
next to the exe. A checkout keeps using the `config.json` in the repository root. Starting `GlassLink.exe` while
the DMC runs opens its status window; the Start menu entry does both.
