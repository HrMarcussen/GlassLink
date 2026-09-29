# Design notes and findings

What was learned building GlassLink, by topic, so nobody has to find it out again. The CHANGELOG says when each
thing changed; this file says why things are the way they are.

## The sim and its pop-out windows (MSFS 2024, Fenix A320)

- **Pop-outs are the capture source.** Right-Alt + click on a display is the only way to get one. Fenix's own
  display renderer (`FenixDisplay.exe`) has a ProSim-style "Displays" menu compiled in, but its windows are hidden
  and render nothing.
- A pop-out is an `AceApp` window owned by the main sim window and shares its title until renamed. **Owned windows
  always stay above their owner**, so an on-screen pop-out covers the cockpit view; but **the sim keeps rendering a
  pop-out that is moved outside the desktop**, and Windows.Graphics.Capture keeps delivering its frames. That is why
  the DMC parks them off-screen (`position`, e.g. x 2600 on a 2560-wide desktop). A plain window (the pygame test
  pattern) stops presenting off-screen; test windows must stay on-screen.
- **A closed pop-out lingers** as a cloaked window that still has its title and counts as visible: windows are
  enumerated without cloaked ones.
- Pop-outs are marked never-activated, or Windows gives the focus back to a parked pop-out instead of the sim.
- The camera for popping out: `CAMERA REQUEST ACTION = 1` resets the cockpit camera and `COCKPIT CAMERA ZOOM`
  brings the displays into view (pitch and yaw are not writable in MSFS 2024). Click points are fractions of the sim
  window after the reset, per aircraft profile, so they work cold and dark and at any resolution with the same
  aspect ratio. Each point records that shape (the built-in Fenix points 16:9, a learned one the user's sim window),
  and a point made on another shape is not clicked: the user is asked to Learn it (a 16:9 point lands elsewhere on a
  21:9 screen, where the camera shows more of the cockpit). The sim ignores injected "save custom camera" keys, but a "load custom camera" key (e.g. Shift+F1)
  works for getting the user's view back.
- **Frame rates.** A pop-out never gets more frames than the sim renders, and pop-outs cost no measurable sim frame
  rate. The Fenix draws its displays at about 20 Hz (19.5 changes a second measured in flight), so more than that
  never reaches a DU. Driver-level frame generation (AMD Fluid Motion Frames via HYPR-RX, NVIDIA Smooth Motion)
  serves only the window in focus and leaves pop-outs with about 13 frames a second; the sim's own FSR/DLSS frame
  generation is fine. The glass cockpit refresh rate Medium was as good as High with the sim above 40 fps.
- **Brightness.** In the current sim and Fenix a pop-out is on or off under the brightness knob, never dimmed (the
  Fenix applies brightness on the 3D model), so the DMC reads the knobs' L:vars through SimConnect
  (`N_DISPLAY_BRIGHTNESS_*`, 0..1; MSFS 2024 accepts `L:` names in data definitions, no WASM or FSUIPC needed) and
  dims the DU. With the Fenix EFB setting *Home Cockpit Mode* on, the pop-outs dim themselves and the DMC stands
  down (it reads `homeCockpitMode` in Fenix's `persistancy.xml`).
- Never end a process that holds window captures on the sim with Task Manager: it was followed twice by AMD "video
  engine timeout" driver resets. Stop the DMC through its tray menu, `GlassLink.exe --quit` or `POST /shutdown`.

## Capture and encode (DMC)

- Only the client area is copied from the GPU; a frame nobody wants is refused before any copy. All captures share
  one D3D11 device.
- JPEG: libjpeg-turbo (TurboJpegWrapper), 1.3 ms for a 768x768 PFD on an i7-8700K. SkiaSharp took 6.9 ms (its
  libjpeg-turbo is built without the assembler routines).
- Picture sizes are whole 16-pixel blocks, rounded down: the DU's decoder writes 16x16 blocks for 4:2:0 and refuses
  an output buffer smaller than that (1080 rows decode as 1088).
- Several displays on one DU go as one **band**: a picture as wide as the DU's screen with the displays on black,
  covering the rows they use. It is composed from the captures' raw pixels (no second JPEG generation) and made when
  a display changed, at most 30 times a second. Windows' default timer ticks every 15.6 ms (`Thread.Sleep(33)`
  sleeps 47 ms), so the DMC asks for 1 ms timer resolution while a band exists.
- **The DU is the clock**: one frame in flight, always the newest; a READY that arrives while nothing new exists
  waits for the next picture, not for a USB read.
- Numbers in the configuration tree are read with `JsonNode.AsDouble()`: `GetValue<double>()` throws for a number
  the program put into the tree itself as an int.
- Measured: six pop-outs captured, two DUs fed, about 10 % of one core and 140 MB.

## The DU (ESP32-P4 + LT8912B)

- **USB.** WinUSB binds without a driver through Microsoft OS 2.0 descriptors, but only *without* configuration and
  function subset headers for a single-function device (with them Windows silently drops the compatible ID, code
  28). Windows caches the result per VID/PID/bcdDevice: bump `bcdDevice` after every descriptor change. Serial
  strings show at most 31 characters, so the serial is 24 hex digits.
- TinyUSB's vendor class in FIFO mode loses data under load: it marks the endpoint free before storing a finished
  transfer, and a read in that moment starts the next transfer for room that is about to be used (16 KB of a frame
  lost, seen as "short payload"). The firmware runs the class unbuffered and keeps received data in its own
  stream buffer, starting a transfer only when a whole one fits. Transfers are 16 KB (the host ends a write of
  n x 512 bytes with a zero-length packet); one transfer per 512-byte packet capped the link at 6.7 MB/s. TinyUSB
  also casts read sizes to 16 bit: a 64 KB read reads nothing.
- **JPEG decoder.** Output rows are padded to whole MCUs (16 for 4:2:0); pixel bytes must be B,G,R for the DSI frame
  buffer (test with red or blue: green/white/cyan patterns hide a swap). The 2D-DMA that carries the decoder's
  output does the YUV to RGB conversion with a programmable matrix; scaling that matrix by the brightness dims for
  free (a linker wrap of `dma2d_configure_color_space_conversion`, the pixel accelerator as fallback).
- **Frame buffers.** Drawing a buffer that lies inside one of the DPI panel's two frame buffers makes the driver flip
  to it instead of copying: full-size pictures are decoded straight into the back buffer. A picture as wide as the
  screen has the frame buffer's row layout and is decoded straight into its rows. Anything else is decoded and
  copied (DMA2D). In 1080p the copy competes with the scan-out (187 MB/s) for PSRAM; full-screen clears still cause
  a short underrun.
- The next picture is received while the current one is decoded; other messages wait for the pictures before them.
- **LT8912B.** It has no reset line on the Olimex adapter and keeps its registers across ESP reboots, so a test
  pattern left on by a diagnostic build masks the picture: the firmware switches it off after init. ESP-IDF's DSI
  defaults (burst mode, EoTp on) give a clean picture; non-burst modes shift it sideways. Registers 0x9c/0x9d
  (line period) and 0x9e/0x9f (vtotal) show whether DSI is locked without a monitor.
- Silicon before rev 3 needs `CONFIG_ESP32P4_SELECTS_REV_LESS_V3` and `CONFIG_ESP32P4_REV_MIN_100`, or the
  bootloader refuses to flash.
- Updates go into the other app slot and are confirmed only after a picture was shown, a host-requested restart, or
  a minute without a crash; three unstable boots in a row fall back to the 768x768 mode.
