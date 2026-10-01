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

### Changed
- The README has pictures: a banner, a "how it works" diagram and the DU's own screens, made from HTML sources in
  `docs/images/src/` by `tools/render-images.ps1`, and screenshots of the status page's Displays, Display units and
  Setup tabs.

### Fixed
- The PFD check before a pop-out took a blue screen without ground under it for a PFD (the FSLabs' EFB tablet, lit
  while the displays were still dark after loading) and then refused to click, every minute, moving the camera each
  time. An attitude sphere now needs brown ground right under its blue sky.
- The same check took the standby horizon for the PFD while the PFD showed no attitude yet (ADIRS aligning, FSLabs
  at a gate on ground power), and again refused to click a view that was right. A sphere narrower than a third of
  the distance between the PFD and ND click points is too small for a PFD and is not counted.
- An aircraft that has just loaded may take a minute or two before every display opens a pop-out (the FSLabs: about
  two). Displays that do not pop out in the first 3 minutes in the cockpit are tried again every 30 s and do not
  count towards giving up. The wait for a pop-out window after a click is 8 s instead of 15 s, so a display that is
  not ready yet keeps the camera away from the user for less time.

## [0.8.0] - 2026-09-30

In short: the FSLabs A321 works out of the box like the Fenix, the installer follows dark mode and can set up
starting with the simulator, click points are only used on the screen shape they were made for, and the old Python
DMC is gone. The DU firmware is unchanged (0.7.0): the DUs need no update.

### Added
- **FSLabs A321 (A32X) profile built in** (#64): the six displays pop out by themselves as with the Fenix (37 s for all
  six; the PFD check recognises the FSLabs PFD). The FO PFD is taken from the FSLabs' own First Officer view, which is
  an instrument view, not the sim's copilot seat: a profile can name its FO camera (`copilot_camera`), and the camera
  is not reset in such a view (the reset would leave it). The FSLabs dims its pop-outs with the cockpit knobs itself
  (measured: the picture follows the knob down to black), so GlassLink does not dim the DUs a second time
  (`popout_dimming.always`).
- The installer can set up "Start and stop with the simulator" (on by default; the same as the tray's option, which
  it still is afterwards), and says what the two ways of starting GlassLink mean. `GlassLink.exe --add-sim-entry`
  does it for the installer, as `--remove-sim-entry` does for the uninstaller.

### Changed
- The installer and uninstaller follow Windows' light or dark mode.
- The status page and viewer moved from `glasslink/static/` to `web/`.
- Pop-out click points know the screen shape they were made on (the built-in Fenix points: 16:9; Learn stores the
  shape of your sim window). On a screen of another shape (21:9, 16:10, triple screens) a point would miss, so it is
  not clicked: the status page says which displays to Learn and why, instead of two clicks that open nothing and a
  "gave up". Any resolution of the same shape works as before.
- The Setup page explains the "camera after a pop-out" key: optional, only for a custom camera saved in the sim, and
  what GlassLink does without it (back to the seat view and zoom you had).

### Removed
- The original Python DMC (`glasslink/`, `start-dmc-python.bat`, `popout-displays.bat` and the tools built on it:
  `auto_popout.py`, `import_popm.py`, `module_probe.py`), retired after the .NET DMC had been the default for a week and
  the Python one had fallen behind (no bands, no self-update, no DU heartbeat) (#77). Git history keeps it. The
  measuring tools in `tools/` and the Pi viewer stay; `requirements.txt` lists only what they need.

## [0.7.0] - 2026-09-29

**[DU firmware]** Update both DUs after installing (the status page offers it): the new DU screens need this DMC, and
this DMC's "waiting for the sim" and goodbye need the new firmware. In short: GlassLink updates itself from GitHub
Releases, the DUs have screens in GlassLink's look that say what is going on, a DU no longer gets stuck after a PC
restart, and the DMC recovers from a sim crash and starts and stops with the sim.

### Added
- **[DU firmware]** The DU's own screens in GlassLink's look (#81): the GlassLink mark and name, the status page's
  dark colours and the Inter typeface (built into the firmware, drawn with smooth edges at any size), and states that
  say what is going on: Waiting for the PC, Waiting for the DMC, Not assigned, *display* · waiting for the sim,
  Identify, and Updating firmware with its progress. The Identify banner over a picture is drawn the same way. A DU no
  longer shows an old picture from an earlier session (e.g. yesterday's PFD while the sim is not running), notices a
  pulled cable, and keeps its label for when no DMC is running. The DMC tells each DU every 2 s what it shows and
  whether the sim is showing it, and says goodbye when it quits.
- **GlassLink updates itself from GitHub Releases** (#76). A minute after the start and every six hours the DMC asks
  GitHub for the latest release (anonymously; `updates.check: false` switches it off). A newer one is announced on the
  status page and in the tray menu, with what is new; **Install update** (the user's click only, and only in an
  installed copy) downloads the setup program, checks it against the release's SHA256SUMS.txt and, once GlassLink is
  code-signed, that it is validly signed by the same publisher, and runs it: GlassLink stops, is updated and starts
  again, and the DUs are then offered their new firmware as usual.
- Releases are built, signed and published by GitHub Actions from a version tag (`.github/workflows/release.yml`,
  `docs/releasing.md`), with the shared steps of `HrMarcussen/release-tools`: the installer, the zip, the DU firmware
  and `SHA256SUMS.txt` on the GitHub Release, the changelog section as notes. `tools\build-release.ps1` gains
  `-Stage publish|package` so the program can be signed before it is packed.

### Changed
- The status page uses Inter too, the typeface of the DU screens, served by the DMC itself (it works offline).
- "Start and stop with the simulator" checked live: the sim starts GlassLink, and GlassLink stops about 18 s after the
  sim quits, the DUs then showing that they wait (#71).
- Releases include `SimConnect.dll` from the MSFS 2024 SDK (1.6.9) instead of the older one from an MSFS 2020-era SDK;
  tested on MSFS 2024 with connection, cockpit detection, brightness L:vars, automatic pop-out and camera return (#79).

### Fixed
- **[DU firmware]** A DU could stop taking data after the PC restarted while the DU stayed powered: it showed up on
  USB, but every write timed out until it was power-cycled. A USB bus reset followed quickly by a new configuration
  (as a booting PC does) went unnoticed, and the DU kept waiting for a transfer the reset had thrown away. The DU now
  counts the host's (re)configurations and starts over on every new session. A task watchdog restarts a DU whose
  protocol or picture task hangs (30 s), prints where on the serial console, and the DMC's log then says why the DU
  restarted (#80).
- After the sim crashed or was ended, the DMC kept the old SimConnect connection with the last values (still "in
  cockpit", brightness frozen) and never connected again: a sim that goes away that way sends no quit message. Now a
  quiet connection without a sim process is dropped within seconds, and the DMC reconnects and pops the displays out
  again when the sim is back (#72).
- The installer's "Start the GlassLink DMC now" started it with the installer's administrator rights; now as the user.
- **[DU firmware]** Clearing the screen (layout changes, idle screens) no longer makes a 1080p screen flicker: the
  pixel accelerator fills the frame buffer on screen in one pass, in 32-byte bursts, instead of the CPU filling a
  6 MB work buffer that was then copied over. Display underruns in 18 layout changes: 13 before, 2 now (#75).

## [0.6.2] - 2026-09-28

### Fixed
- The release's `SimConnect.dll` is a file of its own next to `GlassLink.exe` again, so it can be seen and replaced
  (THIRD-PARTY-NOTICES.md): the single-file build had packed it into the exe (0.6.0 and 0.6.1). The release script
  also looks in `dotnet\lib` itself.

## [0.6.1] - 2026-09-28

### Added
- **Licence: GNU GPL version 3 or later** (`LICENSE`), with an additional permission for Microsoft's SimConnect.dll,
  the WebView2 Runtime and the Visual C++ runtime; future board designs CERN-OHL-S-2.0 (#74).
- `THIRD-PARTY-NOTICES.md`: the components of others and their licences, shipped with releases.

### Changed
- Releases include Microsoft's `SimConnect.dll` again, unmodified, as MSFS add-ons usually do (it stays out of the
  repository, #58): an installed GlassLink needs no MSFS SDK. `tools\build-release.ps1` takes it from `dotnet\lib` or
  an installed SDK and puts `LICENSE` and the notices next to it.

## [0.6.0] - 2026-09-28

**[DU firmware]** Update both DUs (the status page offers it). In short: several displays on one DU (layouts with
test cards, sent as one band), HDMI modes up to 1080p chosen from the status page, DUs three times as fast (768x768
at 60 fps whatever the brightness, a PFD + ND pair on a 1080p monitor at 26-30 fps), firmware updates with rollback,
the request guard and many fixes from the code review of 28 Sept 2026, and `SimConnect.dll` no longer in the
repository (install the MSFS SDK or copy the DLL to `%LOCALAPPDATA%\GlassLink`).

### Added
- **Two DUs on one hub, verified 21 Sept 2026** (DU2 flashed with 0.5.0 over its serial port, both on the DMC):
  each DU on its own display 29.5 and 30 fps of a 30 fps source, both on the same display 29.5 and 28, assignments
  swapped under load 29.5 and 29, no frames dropped, transfer 3.8-4.0 ms per frame with both running (the same as
  one alone), decode 6 ms, draw 12-13 ms; DMC 19 % of one core for two displays at 30 fps, 11 % when both DUs share
  one display (one capture, one encode). Done without the sim: `tools/test_pattern.py --title ...` runs several
  moving test windows, and a bench configuration points two displays at them (windows must stay on-screen: a
  window parked off-screen is only composed by Windows when it is a 3D swap chain like the sim's pop-outs).

- **Assign a DU from the display's card**: every display on the Displays tab says which DU shows it and has a
  "Put on a DU…" list (each DU with what it shows now, or "free") and "Take off DU…". The DU that got the display
  shows its name for three seconds, so you see which physical unit it was. The Display units tab works as before.
- **Getting started checklist** on the status page until a setup has been complete once: displays set up, sim in the
  cockpit, every display popped out, a DU connected, every DU showing a display. Done steps carry a tick, the step
  to do now an arrow and its hint, later ones a circle (never colour alone). It closes itself when all five are
  true, can be hidden, and the System tab brings it back.
- System tab says in words how firmware updates work ("DU firmware this DMC expects", "Firmware image on this PC";
  nothing is fetched from the internet).

- **[DU firmware] `SET_MODE` (0x09)**: the host sets the DU's HDMI mode (0 = 768x768, 1 = 1024x768, 2 = 800x600,
  3 = 1280x720); the DU stores it and restarts into it, and keeps it across normal boots (the bring-up code used
  to reset it to 768x768 at every start). The first step towards a DU on an ordinary HDMI screen. Bench:
  `GlassLink.Bench mode 3 --serial <prefix>`, `stream --size 1280x640 --only <prefix>`.
- **[DU firmware] Tiles: several displays on one DU** (`SET_LAYOUT` 0x0A, `TILE` 0x0B). The host gives the DU a
  layout (up to six rectangles of its screen) and then sends each display as a frame of its own for its tile, so a
  change on one display costs only that display's pixels, whatever the screen size. Test cards (`SET_LAYOUT` with
  bit 0) draw every tile as a bright border with its number and size, for lining the tiles up with a panel's
  cutouts; the last picture of every tile is kept and redrawn after Identify or a brightness change. Measured on
  DU2 in its 1280x720 mode: two 640x640 tiles 48 tiles/s in total (decode 5.6 ms, draw 11.2 ms, transfer 2.5 ms
  each), four tiles of 432x432 and 1280x288 77 tiles/s, one 768x720 tile 36/s. Bench:
  `GlassLink.Bench tiles --serial <prefix> --layout 0,0,640,640;640,0,640,640 [--cards]`. Picture sizes must be
  multiples of 8 (the ESP32-P4's JPEG decoder: "Picture sizes not divisible by 8 are not supported"); positions
  are free. The display editor rounds `client_size` to 8 accordingly.
- **[DU firmware] HDMI mode 4 = 1920x1080 at 30 Hz** for an ordinary monitor behind a panel (two DSI lanes carry
  no more than 30 Hz at that size; one frame buffer). Measured on DU2 without a screen: a 768x768 tile 31 tiles/s
  (decode 9 ms, draw 18 ms with the DMA2D copy, which the 1080p configuration lacked at first: 56 ms and 14/s
  without it); two busy 768x768 tiles share that, 15 each. Enough for one busy display and one quiet one; the
  DU-side pipelining in the backlog would about double it. To be seen on the Arzopa.
- **A DU with several displays** (.NET DMC): a DU's entry gets `screen` (the HDMI mode it is asked for) and `tiles`
  (display -> position); the DU manager keeps the connected DU in step (mode first, then the layout with the
  displays' sizes, then which display feeds which tile), leaves out tiles that do not fit the screen and says so,
  and the brightness knob of the first tile's display dims the whole screen. Status page: "Several displays…" on a
  DU that can (Screen dropdown, the screen to scale with the tiles in it, drag or arrow keys, x/y fields, "Show
  test cards on the DU"). Verified on the sim: DU2 in 1024x768 with the Captain PFD as a tile at 128,0 fed from the
  real capture; test cards on and off; back to a single display.
- **Measured: one DU driving two displays side by side** (DU2 in its 1280x720 mode, test pattern, 22 Sept 2026):
  768x768 41.5 fps (decode 6 ms, draw 13 ms), 1024x704 29 fps (10 / 20 ms), 1280x640 26 fps (11 / 22 ms),
  1280x720 23 fps (12.5 / 25 ms); transfer 3-4 ms throughout. Decode and draw scale with the pixels and run in
  series, so a full-size 1536x768 pair would be about 19 fps: at the Fenix's ~20 changes a second, no headroom.
  A 1280x640 pair (two 640x640 displays on a 720p screen) has some. Pipelining decode and draw in the DU
  (backlog) would roughly double these.
- **A lighter stream for phones** (.NET DMC): `/ws/<name>?max=384&quality=70` re-encodes the frames smaller for
  that one client (about 3 ms a frame, never on the DU path). The viewer page passes `?max=` and `?quality=` on,
  and on a screen smaller than the display (a phone) asks for its own size by itself: about a quarter of the data
  over Wi-Fi, same frame rate. Measured on the sim: a PFD frame 49 KB full size, 18 KB at 384 px, 12 KB at 384 px
  and quality 60.
- The .NET DMC listens on IPv6 as well when the configuration says `0.0.0.0`: `localhost` on Windows tries `::1`
  first and connected only after a 2 s fallback; now 0.01 s.
- **Endurance, .NET DMC** (22 Sept 2026, sim idle in the cockpit, six displays captured, two DUs fed, one hour, a
  sample every 30 s): 5.6-9.8 % of one core (average 7.3), working set 176 MB at the start and 187 MB at the end
  (a brief 227 MB during stream tests), private bytes 106 -> 113 MB, threads 43-52, handles 1288-1329 ending lower
  than they started: no drift.
- **The .NET DMC is the default DMC** (22 Sept 2026): `start-server.bat` starts `GlassLink.exe`; the Python DMC is
  started with `start-dmc-python.bat` and stays as the reference implementation and test tool. Every layer of the
  port was verified against it on the sim and on both DUs before the switch (two DUs at 30 fps, firmware update,
  cold-start pop-out of all six displays, Learn, the brightness link, the status page).
- **.NET DMC: release build and installer.** `tools\build-release.ps1` publishes one self-contained `GlassLink.exe`
  (nothing to install first) with the status page, the DU firmware image and `config.example.json` as a folder and
  a zip under `dist\`, and with Inno Setup 6 installed the setup program `GlassLink-<version>-setup.exe`
  (`installer\GlassLink.iss`: Program Files, Start menu, optional start at sign-in and firewall rule). An installed
  copy keeps its configuration and logs in `%LOCALAPPDATA%\GlassLink` (created from the example on first start).
  `GlassLink.exe --quit` stops the running DMC gracefully and waits until it has gone (used by the installer). The
  exe carries an icon (`tools\make-icon.ps1`), also shown by the status window in the taskbar.
- **.NET DMC: start and stop with the simulator.** Tray menu item "Start and stop with the simulator": an entry in
  the sim's `exe.xml` (MSFS 2024 Store or Steam, MSFS 2020 too), written only when you click it, with a copy of the
  file kept as `exe.xml.before-glasslink` the first time. The sim then starts the DMC with `--with-sim`, and a DMC
  started that way quits by itself when the sim's process has gone. Started twice (with Windows and by the sim) the
  second one simply ends.
- **.NET DMC: `max_size`** ported (a display's picture is shrunk by area averaging before the encode, as the Python
  DMC does with cv2.INTER_AREA), and **Learn verified on the sim** in .NET (copilot view, click detected, point
  stored in the profile, window parked and captured, camera back with Shift+F1).
- `config.example.json` includes the FO PFD and FO ND, so a first start has all six Airbus displays.

- **.NET DMC, layer 1** (`dotnet/`): protocol and resyncing message reader, a native WinUSB transport (no libusb),
  the DU connection (newest-frame flow, Identify, ping, brightness, health, firmware update) with tests against an
  in-process DU, and a bench tool. Verified on both DUs: 30.0 fps to each at 3.4 % of one core, ceiling 41-43 fps,
  firmware update in 3.5 s. Layer 1b: the DU manager (hot-plug, assignments, labels and trim from the shared
  `config.json`, brightness = cockpit knob x trim). Layer 2: window finder, Windows.Graphics.Capture, change
  detection, libjpeg-turbo (1.3 ms per frame), capture on demand; two 30 fps test windows to two DUs cost 13-15 % of
  one core and 105 MB (Python: 19 %, 285 MB). Layer 3: native SimConnect client, aircraft profiles, pop-out procedure,
  automatic pop-out, return to the user's view, brightness link; verified on the sim with a cold start of all six
  displays. Layer 4: `GlassLink.exe`, the DMC as a tray program with the same HTTP API, status page, display editor,
  Learn, advice and firmware update as the Python DMC (28 tests). Either DMC can be run; they share `config.json`.

### Changed
- **[DU firmware] Faster DUs: 768 x 768 from 20 to 57-60 fps, dimmed ones for free, 1080p bands at 25-30 fps.**
  Measured 28 Sept 2026 on DU2 with the bench tool and PFD-like frames of 138 KB (`stream --busy`), old firmware on
  DU1 against new on DU2:

  | | old | new |
  |---|---|---|
  | 768 x 768, brightness 100 | 20 fps (transfer 24 ms, decode 8, copy 13) | 60 fps (transfer 5, decode 8, no copy, overlapped) |
  | 768 x 768, brightness 40 | 17.6 fps (dimming 20 ms) | 60 fps (dimming free) |
  | 768 x 768, 4:4:4 | | 40 -> 60 fps with the overlap |
  | 1080p, two 768 tiles | 11.9 fps per tile | 30 fps for both (the panel's refresh), sent as one band |
  | 1080p, full-width picture 1920 x 1072 | 9.9 fps | 21.6 fps |

  - USB receive in transfers of up to 16 KB instead of one per 512-byte packet: 28 MB/s instead of 6.7. The host
    must end a write of n x 512 bytes with a zero-length packet; both DMCs always did.
  - The next picture is received while the current one is decoded (#61): READY goes out as soon as a picture is
    handed to the drawing task, so the transfer no longer adds to every frame. Other messages wait until the
    pictures sent before them are drawn, so the order is unchanged.
  - The USB vendor class runs unbuffered and the DU keeps received data itself: TinyUSB's FIFO mode marks the
    endpoint free before it has stored a finished transfer, and a read in that moment started the next transfer
    for room that was about to be used, so 16 KB of a frame was lost now and then (a "short payload" and a 3 s
    stall on the DU, a write timeout on the host). The overlap made it frequent; found with an A/B run that counts
    errors on the serial console.
  - A full-size picture is decoded straight into the frame buffer that is not on screen and the panel flips to it:
    no copy and no tearing. The same for a picture as wide as the panel (a band), which goes straight into its rows
    of the frame buffer on screen. 1080 rows of 4:2:0 decode as 1088 and do not fit, so a full 1080p picture only
    takes this path with `subsample_420` off or 1072 rows.
  - Dimming happens in the decoder's own YUV to RGB conversion (its matrix scaled by the brightness) instead of a
    second pass over the picture; the pixel accelerator and then the CPU remain as fallbacks. That also frees the two
    full-screen buffers the old blend needed, so 1080p now has two frame buffers and 12 MB of PSRAM left (#22).
  - The last frame is kept by swapping buffers instead of copying it.
  - INFO `caps` gain `band`.
- **Several displays on one DU go as one band** (a DU with 0.6.0 firmware): the DMC puts all tiles of the DU's
  layout side by side on black into one picture as wide as the screen, covering the rows they use, and the DU
  decodes it straight into its frame buffer. In 1080p a PFD and an ND of 768 x 768 behind a MIP went from 12 fps
  each to 28 fps for both (26.5 with a screen full of text, 28 Sept 2026, through the DU manager). The displays come
  from their raw capture, not decoded again from JPEG; a band is made when a display changed, at most 30 times a
  second, so displays that change together go out together. Nothing changes in the layout editor or on the status
  page, and test cards are still drawn per tile. Firmware without `band` in its INFO gets one TILE per display as
  before.
- **No DU setup below 20 fps without a word** (#62):
  - A single display narrower than the DU's screen goes as a band too (the picture centred on black, as wide as the
    screen): a 1056 x 1056 PFD on a 1080p screen 17.5 -> 22-26 fps.
  - Display sizes are rounded down to whole 16-pixel blocks, never up: a full-screen 1920 x 1080 display becomes
    1920 x 1072, which a 1080p DU draws straight into its screen (26 fps), where 1088 rows were refused as larger
    than the screen. The same for `max_size`, in both DMCs.
  - A DU that gets pictures faster than it draws them and still shows fewer than 20 a second is reported on the
    status page, with what helps (firmware with bands, smaller pictures, fewer displays, a lower HDMI mode), apart
    from the USB advice.
- A display inside a band counts as shown, so its capture keeps its full rate (it dropped to the idle rate, and the
  band with it).
- `SimConnect.dll` is no longer in the repository or its history (#58), and releases do not ship it: the DMC finds
  it next to GlassLink.exe, in `%LOCALAPPDATA%\GlassLink\`, or in the MSFS SDK (`MSFS2024_SDK` / `MSFS_SDK`). A
  developer's own copy in `dotnet/lib/` (ignored by git) is still copied next to the exe when building.
- **Documentation rewritten for users**: the README says what GlassLink does, how to install it and how to configure
  it; the engineering log became `docs/notes.md` (findings by topic) and `docs/hardware.md` (parts, wiring, scaler
  board facts); `docs/BACKLOG.md` became GitHub issues; the .NET and firmware READMEs describe the current state.
- The status page says "SimConnect.dll is missing" with what to do, instead of waiting for the sim for ever.
- Bench tool: `--busy` (gradients and a screen of text, for PFD-sized frames), `--444` and `--brightness`; its test
  pictures are now encoded with the DMC's encoder; `manage` shows the DU's decode, draw and transfer times.
- **Status page, a pass with UX glasses on.** Displays carry the names a builder uses (Captain PFD, Upper ECAM, FO ND;
  the id stays beside it) and, on a wide window, sit in their cockpit positions with the lower ECAM under the upper
  one. A card says which DU shows it; window handle, backend and frame counters are behind one "technical details"
  switch. DU rows: label, state, display, brightness ("cockpit knob 25 % -> panel 25 %"), picture ("19 fps, 32 ms
  per picture" or "not keeping up" with the reason), firmware version; Identify is the one prominent button, an
  Update button appears only when an update is available, and Ping, Reinstall firmware, Forget and the hardware
  facts are under "More". Setup rows: one Learn button for the seat the display is clicked from (the other seat,
  Close window and Remove under "More"), Save appears when something was changed. The pop-out pill shows only while
  something is happening; the build id moved to the System tab, which also shows the sim's frame rate; no footer.
- **One bar instead of two.** The status page's logo line and tab line are one bar now (brand, tabs, the three status
  pills, clock); the bar stays while the rest scrolls beneath it, so more of the cockpit fits without scrolling.
- **.NET DMC: a window of its own for the status page** (WebView2) without the Windows title bar: the page's bar is
  the title bar. It drags the window, a double click maximises, and it carries minimise, maximise and close; resize
  borders, shadow, rounded corners and snap are kept. Own icon in the taskbar, remembers its place, size and text
  zoom (Ctrl + wheel), opens at a size that follows Windows' text scaling. Closing it frees the browser, the DMC
  runs on in the notification area. Starting GlassLink.exe a second time opens this window instead of an "already
  running" message. Without a WebView2 runtime it falls back to Edge's app mode, then the default browser. The page
  stays reachable from any browser on the network (a phone, another PC) exactly as before.
- **.NET tray menu** follows Windows' light or dark mode, in the colours of the status page, with rounded hover and
  corners, the system's menu font and text size, and the three status lines with a symbol and a colour each.

### Fixed
- **Housekeeping from the code review** (issues #56, #57, #59, part of #58): the build is free of warnings (the
  WebView2 WPF reference is dropped for every project; a platform-neutral serial helper), and CI builds and tests the
  .NET DMC, compiles the Python code and builds the DU firmware on every push, each only when its part changed. The
  single-instance mutex is released only after the DMC has closed its captures and DUs, so `--quit` and the installer
  wait for a complete stop; `tools/stop_server.py` finds the DMC before stopping it and waits for the process (it
  could hang on a Windows management query). SimConnect.dll is also found in an installed MSFS SDK. Documentation
  brought in line (protocol host and DU behaviour, the pop-out's camera settings, the .NET DMC as the default).
- **Python reference DMC, from the code review of 28 Sept 2026** (issues #48-#54):
  - It refuses to start while the .NET DMC runs or the port is taken, before any capture starts (it used to start
    the captures, fail to bind the port and exit with capture sessions of the sim still open); a capture worker
    always takes its session with it when it stops.
  - config.json keeps everything: all sections are merged with the defaults instead of some being replaced
    (`process`, `brightness`, `firmware` and keys it does not know used to be lost at its next save); options on
    the command line are no longer saved; saves go through a flushed temporary file with a .bak, one at a time.
  - It sends an empty layout to DUs that can show tiles (so a layout left by the .NET DMC ends), answers 501 for
    screen modes and layouts, reports `engine: python` and its limitations (the status page then hides the layout
    editor), and removing a display also removes it from DU layouts.
  - Module and display edits run off the event loop; /status iterates over copies; a pop-out that stops early (not
    in the cockpit, no profile) no longer resets the camera or presses the restore key; `snapshot` stops its
    capture also on Ctrl+C; `::`/`*` listen on every interface like the .NET DMC.
  - Tools find either DMC by the process on its port; `stop_server.py` waits until the process has ended, not only
    the port. `tools/probe_lvars.py` (FSUIPC WebSocket) and dead code removed.
- **Viewer** (issue #14): a hidden page (another tab, a locked phone) closes its connection, so the display drops
  back to the preview rate; phones ask for at most 512 pixels; the picture fills the screen; a removed display says
  so instead of reconnecting for ever; the MJPEG mode reconnects.
- **.NET DMC robustness, from the code review of 28 Sept 2026** (issues #11, #29, #30, #32, #35-#47):
  - Shutdown from /shutdown, `--quit` or "stop with the simulator" is handed to the UI thread: with the status
    window open the DMC used to keep running (and the installer said it could not stop it).
  - The single-instance check treats a mutex left by an ended DMC or by a status window as free: a new start is a
    DMC again, not one more status window.
  - One camera lock for the automatic pop-out and Learn, held until the user's view is back: they can no longer
    move the camera together (which could store the pop-out's own click as the user's). Learn shows its result after
    the view is restored, and the restore messages no longer replace it.
  - Right-Alt, mouse button and restore keys are always released, also after an error; quitting cancels Learn and
    lets a running pop-out stop after its current display with the view restored. Clicks land on the right spot
    when the sim runs on a monitor other than the primary one.
  - config.json is saved through a temporary file flushed to disk, with the previous one kept as config.json.bak;
    an empty or broken config.json (a blue screen during a save) falls back to it; a failed save takes the change
    back. Text values of the wrong type in a hand-edited file are read as defaults, not as a crash every 100 ms.
  - After a graphics driver reset every capture notices the lost device and starts again with a new one, instead of
    freezing until the DMC is restarted.
  - exe.xml files in Windows-1252 (as FSUIPC writes them) are read and saved in their own encoding; the entry goes
    into every installed sim's exe.xml; duplicate entries are removed; a file without our entry is not touched.
  - Concurrent edits of one display cannot leave a second capture running; SimConnect's last call outside its lock
    moved in and its thread survives any error; unhandled errors are logged and shown; a taken port is shown; the
    first start of an installed copy creates its configuration after taking the single-instance mutex.
  - Uninstall removes the "Start with Windows" entry also when it was set from the tray, and GlassLink's entry in
    the sim's exe.xml (`GlassLink.exe --remove-sim-entry`); the firewall rule is not duplicated on upgrades.
  - A maximised status window leaves an auto-hide taskbar reachable; tray menu font and window icon are made once.
  - The slow-source advice stays 8 s after the rate is back, so it does not flicker; DU messages are read with a
    DU-to-host filter (at most 64 KB, DU types only); process names are looked up again after 30 s.
- **[DU firmware] Tiles, 1080p and robustness, from the code review of 28 Sept 2026** (issues #15-#21, #23, #24):
  - Picture sizes are multiples of 16, not 8: the ESP32-P4's JPEG decoder writes 4:2:0 pictures in 16 x 16 blocks.
    The DU now allocates its decode buffers in whole blocks (a full 1920x1080 frame failed every time: it decodes
    as 1920x1088) and packs padded rows (a 440-wide picture was drawn sheared). Both DMCs round `client_size`,
    tiles and `max_size` output to 16. Measured on DU2: 1920x1080 full frames 9 fps (decode 35 ms, copy 64 ms),
    a real 440x440 picture decodes cleanly.
  - A frame larger than the DU takes (512 KB, 1 MB in 1080p; INFO `max_frame`) is skipped by its length with a LOG
    and a READY instead of a 3 s resync; the DMC does not send such frames at all. Unknown message types are skipped.
  - Tile mode ends with a new USB session or a whole-screen FRAME, and the DMC sends the layout (also an empty one)
    after every INFO: no old tiles over a live picture. Brightness changes redraw only while a picture is up (no
    old cockpit picture over NOT ASSIGNED), and leaving an idle screen clears it first.
  - All drawing under one lock (idle screens could collide with a frame); a rejected tile keeps its slot number;
    stale tile pictures are dropped when a rectangle changes; more than 6 tiles are refused with a message.
  - A new firmware image is confirmed only after it has shown a picture, after a restart the host asked for, or
    after a minute without a crash; before that the bootloader rolls a crashing image back. INFO `confirmed`.
  - USB starts before the display; a display that cannot start leaves the unit reachable (INFO `display_error`),
    and three crashes in a row fall back to the 768x768 mode.
  - Smaller items: READY and OTA results on every path, the OTA timeout also when unplugged, a new ident label
    while ident is on, the ident banner on test cards, the flip waits until the old frame buffer is free (no
    tearing), idle screens use a small band of memory, tile copies are sized to their pictures.
  - A DU without a stored serial derives it from the chip's factory MAC (existing units keep theirs).
- **.NET DMC, tiles** (issues #25-#28, #31, #33): what a DU shows is one immutable snapshot, so a layout change can
  no longer throw on the DU's thread or let a whole-screen FRAME follow a SET_LAYOUT; a tile without a picture is
  waited on instead of polled; tile sizes come from the picture really published; mode requests are tracked per
  connection (no duplicate request, no false "stayed in mode"); switching to tiles without a NOT ASSIGNED flash;
  health warnings for tiled DUs with hysteresis (two bad reports in a row to show, three good ones to clear); the
  screen is synced as soon as INFO arrives; dead DUs are disposed outside the manager's lock and brightness never
  queues behind it; a DU's transport waits for a running write before it is freed; frames go out without being
  copied into a new array.
- **Security, from the code review of 28 Sept 2026** (issues #1, #3, #4), both DMCs:
  - A request guard in front of the API: requests must be addressed to this PC (no DNS rebinding), cross-site
    Origins are refused, POSTs must be JSON (no cross-site request forgery from a web page), and changes come from
    this PC only unless `server.allow_lan_control` is true. The phone keeps reading the page, pictures and viewer.
  - The status window opens only the DMC's own viewer links in the browser and never navigates away from the
    DMC; developer tools only in debug builds.
  - Input checks: DU serials must be hexadecimal and known, labels at most 32 characters, displays must exist,
    `alt+f4` is refused as the restore key; the Python DMC checks brightness and rotation before saving them and
    answers 501 for screen modes and layouts. A short serial no longer breaks the tray status.
- **Status page, from the code review of 28 Sept 2026** (issues #2, #5-#10, #12, #13):
  - The Display units tab threw on the new layout rows after every change: DU2's controls stopped working and the
    header flashed "DMC not reachable" (the jitter seen on 22 Sept).
  - Every value from the DMC, the sim or the user is escaped before it becomes HTML (a DU label could run script).
  - Layout editor: tiles are visible again (an undefined colour), sized by the display's picture size even before
    the sim runs, update while the row is open, are clamped to the screen and marked when outside it, and at most
    six can be ticked; dragging saves only after a real move; the screen mode changes only with Apply.
  - Displays shown as tiles count as shown everywhere ("Shown on", "Put on / Add to", checklist, advice).
  - Arrow keys on a closed dropdown no longer apply every step; the choice applies on Enter or when leaving it.
  - The learn result disappears after 30 s or with Dismiss.
  - Contrast of borders raised to 3:1, keyboard focus is kept after a button press, labels name their display,
    tabs carry aria-current, bigger touch targets on narrow screens, the checklist uses the sim's own "in cockpit".
- The "slow source" advice is only given for windows of the simulator; a test pattern or another program's window
  may be slow without the DMC blaming the sim.
- A DU's dropped-frame count from before the DMC connected was reported as new drops ("stall") at connect.
- **.NET DMC, a review pass before it became the default** (22 Sept 2026), nine findings fixed, none seen in use
  yet: the WinUSB handles were freed before the reader thread had stopped (a crash possible at quit or unplug); a
  JPEG encode could still be running while its encoder was freed after a settings save; a DU's thread could end the
  process on an unexpected error; SimConnect wait handles were disposed under a thread still using them, and two
  threads could be inside the SimConnect library at once; the "stop with the sim" timer was the only unguarded
  timer; a closed pop-out's capture object was kept alive by the capture item (a small leak per re-pop-out) and a
  late "closed" event could stop the new capture; the advice rates and the configuration tree were read on one
  thread while written on another; a DU scan could run after the manager was disposed, and every capture callback
  queued behind a DU being unplugged; `max_size` allocated two large buffers per frame.

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
