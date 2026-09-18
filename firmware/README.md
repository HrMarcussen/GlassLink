# GlassLink module firmware (ESP32-P4)

Target hardware: Waveshare ESP32-P4-NANO + Olimex MIPI-HDMI adapter (Lontium LT8912B) feeding the HDMI-to-LVDS
scaler board of an 8.82" 768x768 panel. Protocol: `../docs/usb-protocol.md`. Host side: `../glasslink/modules.py`.

## What it does
- Generates a 32-hex-character serial GUID on first boot (stored in NVS) and reports it as the USB serial string.
- Enumerates on the ESP32-P4's high-speed USB port as vendor-specific device `303A:4001` "GlassLink DU" with
  Microsoft OS 2.0 descriptors, so Windows binds WinUSB without a driver.
- Drives the LT8912B over I2C (GPIO7 SDA / GPIO8 SCL through the DSI flat cable) and MIPI-DSI (2 lanes, 960 Mbps)
  in one of three HDMI modes: 768x768@60 (custom, default), 1024x768@60, 800x600@60 (NVS key `mode`).
- Receives JPEG frames over bulk USB, decodes them with the hardware JPEG engine into RGB888 and shows them centred.
- Idle screens without a font library: NO USB / NOT ASSIGNED (with the serial) / IDENT.
- Brightness (software), rotation, ping, stats every 2 s, OTA into the second app slot with rollback protection.

## Build and flash
ESP-IDF v5.5.x installed by the Espressif Windows installer (`C:\Espressif`). Open the "ESP-IDF 5.5 CMD" shortcut
or run `C:\Espressif\idf_cmd_init.bat esp-idf-v5.5.5` in a terminal, then:

```
cd firmware
idf.py set-target esp32p4
idf.py build
idf.py -p COMx flash monitor      # COMx = the NANO's USB-C (CH343) port
```

The first `idf.py build` downloads the two managed components (`espressif/esp_tinyusb`, `espressif/esp_lcd_lt8912b`).

## Bench wiring (NANO development board)
- USB-C: power + flashing/monitor (CH343 UART).
- Type-A socket: the module link. This socket is the P4's high-speed USB port and also outputs 5 V, so use an
  A-to-A cable with the red (5 V) wire cut, to a hub port on the sim PC.
- DSI 15-pin flat cable to the Olimex adapter, HDMI to the scaler board, 12 V to the scaler board.

## Bring-up order
1. Flash, open the monitor: expect "HDMI mode 768x768 ready=1" and the NO USB screen on the panel.
   If the scaler board shows nothing at 768x768, set NVS `mode` to 1 (1024x768) via `idf.py monitor` console
   or temporarily change the default in `main.c` and retry.
2. Connect the A-to-A cable: Windows shows "GlassLink DU" under Universal Serial Bus devices; the server's
   status page lists the serial; the panel shows NOT ASSIGNED with the serial.
3. Assign a display on the status page: frames flow, STATS show fps and decode time.
4. Two modules on one hub: both listed, independently assignable.

## Bring-up log
- 2026-09-14, module 1 (NANO, chip rev v1.3, serial 1501f789b4fad0865624c341):
  - sdkconfig must select the pre-3.x silicon (`CONFIG_ESP32P4_SELECTS_REV_LESS_V3=y`, `CONFIG_ESP32P4_REV_MIN_100=y`),
    otherwise the bootloader refuses to flash.
  - LT8912B detected over the DSI cable's I2C, HDMI enabled, 768x768 mode set (ready=0 without a monitor: no HPD).
  - WinUSB binding needed the MS OS 2.0 descriptor set *without* configuration/function subset headers (single-function
    device); with subsets Windows silently drops the compatible id (code 28). Windows caches the result per
    VID/PID/bcdDevice: bump `bcdDevice` after every descriptor change.
  - USB serial strings show at most 31 chars on Windows -> serial GUID is 24 hex chars.
  - Throughput: TinyUSB debug level 2 (per-packet console prints) limited the link to ~55 KB/s; level 0 -> 9 MB/s.
    CPU copy into the DPI frame buffer cost ~45 ms per 768x768 frame; `flags.use_dma2d` -> free. Result: 54 fps with
    15 KB test frames, rx 1.9 ms, HW JPEG decode 13 ms per frame.
  - Known: transfers larger than ~128 KB in one message stall (256 KB took 16 s); frames are <= 64 KB so not hit.
- 2026-09-14 evening, panel bring-up (SOLVED):
  - Plain white panel = no LVDS data from the scaler; that was the bench 12 V supply / plug, not the module.
  - Then black or "no signal" for every mode while the bridge's own test pattern showed: the LT8912B has no reset line
    on the adapter and keeps its registers across ESP reboots, so the pattern generator (cec page 0x70 bit 7) that a
    diagnostic build had switched on stayed on and masked the DSI picture. `make_panel` now writes 0x70/0x71 = 0 after
    init. Lesson: never assume the bridge starts clean.
  - With the pattern off, the 768x768 custom timing (48 MHz, hfp 40 / hs 80 / hbp 104, vfp 3 / vs 4 / vbp 20) shows
    a perfect picture with the ESP-IDF DSI defaults (burst mode, EoTp on). Non-burst modes (what the Linux driver uses)
    shift the picture sideways on this chip+host combination, so keep the defaults.
  - Bridge regs 0x9c/0x9d measure the DSI line period (~800 MHz units), 0x9e/0x9f the vtotal; 0xffff right after init
    just means "not locked yet". Handy for checking the DSI link without a monitor.
  - draw_bitmap with DMA2D is asynchronous: `draw_sync` now waits for `on_color_trans_done` before the caller may reuse
    or free its buffer (12 ms per 768x768 frame).
  - End-to-end: server test pattern -> USB -> module -> panel at 29.5 fps, decode 6.4 ms, rx 3.3 ms, 0 dropped.
  - Scaler board OSD: brightness defaults to 100, which lifts the black level and shows a bright halo / mura ring on
    dark content. Brightness 50 (contrast default) gives correct blacks and colours. Set this on every new board.
  - 2026-09-17: first real Fenix PFD on the panel from the sim. Colours were R/B swapped: the DPI frame buffer takes
    pixel bytes as B,G,R (little-endian 0xRRGGBB). Fixed by decoding JPEG with JPEG_DEC_RGB_ELEMENT_ORDER_BGR and
    writing B,G,R in the drawing code. The green/white/cyan test pattern cannot show this swap; test with red or blue.
  - 2026-09-17 with the sim running: two stream bugs found and fixed.
    (1) Host: the worker waited on a threading.Event that stayed set after a send, so with a source slower than
    the module it woke on a stale signal and then sat in a 250 ms USB read -> 4-10 fps instead of the source rate.
    Now a Condition with a predicate (hub.tcond). (2) Module: after a host restart mid-frame the byte stream was out
    of step and the old "drain everything" resync never recovered while frames kept coming ("bad header, resync"
    forever, module at <1 fps). Now the header window slides byte by byte until a valid header (magic, version,
    known type, sane length) lines up. Server restarts go through POST /shutdown (tools/stop_server.py) so
    capture sessions close cleanly.
  - Bench diagnostics stay in the firmware behind NVS keys: `diag` (cycle DSI variants per boot), `dsivar` (0..3),
    `mode` (0..3), plus `display_diag_test_pattern()` / `display_diag_set_dvi()`.
