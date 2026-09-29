# GlassLink DMC <-> DU USB protocol (draft 1, 2026-09-08)

Applies to the ESP32-P4 display modules and to the DMC's USB output path (`dotnet/`; the original Python DMC was
retired in 0.7.1).
The WebSocket path (`/ws/<name>`) is unchanged and stays available for browsers, Pis and testing.

## 1. USB device

| Item | Value |
|---|---|
| Speed | USB 2.0 high speed (480 Mbit/s), ESP32-P4 USB OTG in device mode |
| Vendor / product id | Espressif VID `0x303A` with a PID requested from Espressif's public PID pool for this project; until granted, the development PID `0x303A:0x4001` is used. Both are handled by the server. |
| Manufacturer string | `GlassLink` |
| Product string | `GlassLink DU` |
| Serial string | 24 hex characters (Windows exposes at most 31 characters of a USB string descriptor): a random id generated on first boot and stored in NVS. This is the module's identity. It never changes and is what the app assigns a cockpit display to. |
| Interface 0 | vendor specific (class `0xFF`, subclass `0x00`, protocol `0x00`), one bulk IN and one bulk OUT endpoint, 512-byte packets |
| Driver binding | Microsoft OS 2.0 descriptors (BOS + compatible id `WINUSB` + DeviceInterfaceGUID `{B7E8A4C2-6F0D-4E21-9C3A-5D2F1E8B7A60}`) so Windows 8.1+ binds `WinUSB.sys` automatically. Verified 14 Sept 2026. The descriptor set must not use configuration/function subset headers on this single-function device, and `bcdDevice` must change whenever the descriptors change because Windows caches the result per VID/PID/bcdDevice. |
| Optional interface 1 | CDC-ACM debug console (development builds only; removed in release firmware so the module shows up as exactly one device) |

A module with blank firmware enumerates, reports its serial, shows a "not assigned" test screen on the panel
(module serial, firmware version, USB status) and waits. The assignment lives on the PC:

```json
"modules": {
  "3f9c2b8d4e1a4c7f9d2a6b1e8c5f0a34": { "display": "pfd", "brightness": 100 },
  "a1b2c3d4e5f60718293a4b5c6d7e8f90": { "display": "nd",  "brightness": 100 }
}
```

## 2. Framing

Every transfer on either pipe is one message: a 16-byte little-endian header followed by an optional payload.
Bulk transfers larger than 512 bytes are split by USB automatically; the receiver reassembles by `length`.
A message whose total size is a multiple of 512 is followed by a zero-length packet (ZLP) by the sender. This is
required towards the module: it receives in transfers of up to 16 KB that end on a short packet, so without the ZLP
such a message would sit in the module until the next one arrives.

```
offset  size  field
0       2     magic      'X','D'  (0x58 0x44)
2       1     version    1
3       1     type       see tables below
4       4     length     payload bytes (0 .. 4 MiB)
8       4     seq        frame sequence number (FRAME / READY), else 0
12      4     arg        message-specific
```

## 3. Host -> module (bulk OUT)

| type | name | payload | arg | meaning |
|---|---|---|---|---|
| 0x01 | FRAME | JPEG (baseline, 4:2:0 or 4:4:4, any size up to the panel size) | 0 | Show this frame. Sent only after a READY. |
| 0x02 | GET_INFO | none | 0 | Module answers with INFO. |
| 0x03 | SET_BRIGHTNESS | none | 0..100 | Effective DU brightness = cockpit knob (sim L:var, per-aircraft profile) x the module's trim slider. Sent by the host whenever it changes (a few times per second while a knob turns) and after connect; the module applies it to every frame (software dimming today, backlight PWM later) and does not persist it. |
| 0x04 | SET_ROTATION | none | 0, 90, 180, 270 | Rotation applied by the module. |
| 0x05 | SHOW_IDENT | UTF-8 label of the module (<= 31 bytes, may be empty) | seconds, 0 = off | Stamp an "IDENT <label>" banner with the serial across the top of every frame (live picture or the kept last frame) so the user can see which physical unit this is while assigning. 0 cancels it; the module then redraws the last frame without the banner. |
| 0x06 | PING | none | nonce | Module answers PONG with the same nonce. |
| 0x07 | SET_ASSIGNED | optional (0.7): the DU's label, a newline, the assigned display's name, UTF-8 (`DU1\nCaptain PFD`) | 0 = nothing assigned, 1 = assigned and its pictures come, 2 (0.7) = assigned, but its display has no window (the sim is not showing it) | Sent after INFO, whenever the assignment changes, and (0.7) every 2 s, which also tells the DU that a DMC is there: with no message from a host for 6 s the DU shows "Waiting for the DMC". 0 shows "Not assigned", 2 "<display> · waiting for the sim" instead of an old picture; the DU keeps the label in NVS for its screens. Older firmware reads only arg 0 / not 0. |
| 0x09 | SET_MODE | none | HDMI mode: 0 = 768x768, 1 = 1024x768, 2 = 800x600, 3 = 1280x720, 4 = 1920x1080 at 30 Hz (two DSI lanes carry no more; one frame buffer) | The DU stores the mode and restarts into it (about 3 s); INFO reports it as `mode`. For a DU on an ordinary HDMI screen. A frame smaller than the screen is centred, a larger one refused. |
| 0x0A | SET_LAYOUT | tiles, 8 bytes each: uint16 LE x, y, w, h (up to 6) | bit 0 = show test cards | A screen with several displays on it: the DU keeps the layout in RAM (the host sends it after every INFO), clears the screen and then takes TILE messages. With bit 0 set every tile is drawn as a test card (bright border, number, size) for lining the tiles up with a panel's cutouts; TILEs are kept but not drawn meanwhile. An empty payload ends tile mode. INFO reports `tiles` (the count) and `caps` containing `tiles`. Tile sizes (and so the displays' `client_size`) are multiples of 16: the DU's hardware JPEG decoder writes whole 16 x 16 blocks of a 4:2:0 picture and refuses other sizes (found 22 Sept 2026, #15); positions are free. A DU whose `caps` contain `band` (0.6.0) decodes a picture as wide as its screen straight into the frame buffer, so the DMC sends all tiles of such a DU as one layout tile covering the rows they use, full width, with the displays side by side on black (a "band"); test cards stay per tile. |
| 0x0B | TILE | JPEG | tile index | A frame for one tile of the layout, centred in its rectangle; the rest of the screen is untouched. Same flow control as FRAME (one in flight, READY after it). A tile costs what a frame of its own size costs, whatever the screen size: two 640x640 tiles on a 1280x720 screen run at 48 tiles/s in total, four 432x432 at 77/s (measured 22 Sept 2026). |
| 0x0C | BYE | none | 0 | (0.7) The DMC is quitting: the DU shows "Waiting for the DMC" at once and forgets the pictures of the session. Older firmware skips it as unknown. |
| 0x10 | OTA_BEGIN | none | total image size | Start a firmware update. The DU erases the inactive slot, shows an "UPDATING FIRMWARE" banner, ignores FRAMEs, and answers OTA_PROGRESS 0 (or OTA_RESULT 1). |
| 0x11 | OTA_DATA | firmware chunk (the DMC uses 32 KiB) | offset of this chunk | Must arrive in order. Answered with OTA_PROGRESS = bytes written so far; the host sends the next chunk only then (stop and wait, because flash writes block the DU). |
| 0x12 | OTA_END | none | CRC32 of the image (zlib) | The DU checks size and CRC, lets ESP-IDF validate the image, selects the new slot, answers OTA_RESULT and reboots if it was 0. The new image confirms itself after the display is up; otherwise the bootloader rolls back. |
| 0x20 | REBOOT | none | 0 | |

## 4. Module -> host (bulk IN)

| type | name | payload | arg | meaning |
|---|---|---|---|---|
| 0x81 | READY | none | last displayed seq | "Send me the newest frame." Sent once at start, after every FRAME has been decoded and shown, after every INFO reply, and repeated every 2 s while idle (so a host that connects later, or missed a READY, still starts). This is the flow control: the host never has more than one frame in flight, and always sends the newest one (same latest-only rule as the WebSocket path). |
| 0x82 | INFO | JSON: `{"fw":"0.2.0","build":"d9b66df","hw":"p4-nano+lt8912b","panel":[768,768],"decoder":"hw","uptime_s":1234,"temp_c":41.2}` | 0 | |
| 0x83 | STATS | JSON: `{"fps":29.6,"decode_ms":11.2,"draw_ms":12.9,"rx_ms":6.0,"dropped":0,"free_psram":27189568,"ident":0}` | 0 | Every 2 s and immediately after SHOW_IDENT. `ident` is the module's own view of the banner, which the app uses for the Identify toggle. |
| 0x84 | PONG | none | nonce | |
| 0x85 | LOG | UTF-8 text | level | Debug output, shown in the app's module log. |
| 0x90 | OTA_RESULT | none | 0 ok, 1 begin failed, 2 write failed, 3 image rejected, 4 CRC mismatch, 5 size mismatch, 6 timed out (15 s without data), 7 out of order | Ends an update, successfully or not. |
| 0x91 | OTA_PROGRESS | none | image bytes written so far | Acknowledges OTA_BEGIN (0) and every OTA_DATA. |

## 5. Host behaviour

1. Enumerate all devices with the GlassLink VID/PID (hot-plug aware). Open the WinUSB interface, read the serial.
2. Send GET_INFO. INFO says the screen size (`panel`), the largest frame the DU takes (`max_frame`) and what it
   can do (`caps`). The host sends no frame larger than `max_frame` and, to a DU with `tiles`, the layout (an empty
   one for a single display) - as one band if `caps` contain `band` (see SET_LAYOUT). A picture smaller than the
   screen is centred by the DU; a larger one is refused, so
   the display's `client_size` should not exceed the screen (the host does not scale to fit).
3. Look up the serial in `modules`. Unassigned modules are listed in the app with a "show ident" button; assigning writes the config.
   Send SET_ASSIGNED so the module shows either the picture or one of its own screens, and repeat it every 2 s; send BYE
   when quitting. An unplugged module stays
   listed only if it has an assignment or a label ("Forget" removes that); an unconfigured unit vanishes when unplugged.
4. Loop: wait for READY, then send the newest JPEG of the assigned display if its seq is newer than the READY's seq, otherwise wait for a new frame. This is exactly the WebSocket hub logic with a different transport.
5. On USB error or unplug: close, forget, and pick the device up again on the next enumeration.

## 6. Module behaviour

1. Boot, init panel via LT8912B (768x768@60, 2 DSI lanes). With no picture to show the DU draws its own screens (0.7,
   #81), in Inter from fonts built into the firmware: Waiting for the PC (no USB host, or the bus is suspended: a pulled
   cable looks like that on a DU with its own power), Waiting for the DMC, Not assigned, <display> · waiting for the
   sim, Identify, Updating firmware (with progress). A new USB session, BYE and SET_ASSIGNED 2 make it forget the
   pictures it had, so an old picture never comes back.
2. Start USB; on configuration, send READY.
3. On FRAME: hardware-JPEG-decode (dimmed in the decoder's colour conversion if the brightness is below 100 %), draw.
   READY goes out as soon as the frame is received and handed to the drawing task (0.6.0), so the host sends the
   next one during the decode; at most one frame waits. Messages other than FRAME, TILE, PING and SET_BRIGHTNESS are
   handled after the pictures before them are on screen. A full-size picture whose rows are whole MCUs is decoded into the frame buffer
   that is not on screen and the driver switches to it; the next one waits until the old buffer is free, so nothing
   tears. A picture as wide as the panel is decoded straight into its rows of the frame buffer on screen. Anything
   else is decoded into a work buffer and copied into place. So in 1080p the fast way to show several displays is
   one picture as wide as the panel with the displays side by side, not one TILE each.
   Decode errors are reported with LOG and answered with READY so the stream continues.
   The module keeps a copy of the last frame and redraws it when an overlay (IDENT, idle screens) ends.
   Stream resync: if a header does not validate (magic, version, known type, length), the module slides its
   16-byte header window one byte at a time over the incoming data until one does; this recovers from a host that
   was restarted in the middle of a message.
4. If no FRAME arrives, the last frame simply stays on screen (a "no signal" marker is not implemented). SET_ROTATION
   is stored and reported but not applied yet: the panels are mounted upright.
5. OTA: dual app partitions; the new image is written to the inactive slot, verified, and booted with rollback protection.

## 6a. Reserved for the hardware track (protocol freeze, 18 Sept 2026)

Decided before the hardware exists, so that the .NET DMC can be written against a fixed protocol (version stays 1;
a DU or DMC that does not know these simply ignores them).

- **Backlight needs no new message.** `SET_BRIGHTNESS` already means "effective DU brightness 0..100". A DU with a
  backlight output (PWM + RC filter into the scaler's VR_ADC) applies it there and stops dimming the picture; a DU
  without one keeps blending in the pixel accelerator. INFO reports which: `"dimming": "backlight" | "ppa"`.
  A DU with a backlight output takes 0 as "backlight off".
- `0x08 SET_PANEL_POWER` (host -> DU, arg 1 = on, 0 = off): drives the scaler's DC12V_INC_OFF line. The host sends
  0 when the sim is gone for longer than a configurable time and 1 when it is back; the DU switches on by itself
  at power-up and whenever a FRAME arrives. INFO reports `"panel_power": true | false` on DUs that have the line
  (absent otherwise, and the DMC then never sends 0x08).
- INFO may carry `"caps": ["backlight", "panel_power"]` as the summary of the above.

## 7. Sizes and rates

A 768x768 JPEG at quality 85 is 30 to 60 KB for a Fenix display, up to about 140 KB for a busy picture; a 1080p band
of two displays 100 to 300 KB. At 30 fps that is 1 to 9 MB/s per DU; a DU receives about 28 MB/s (16 KB transfers),
and a USB 2.0 hub shares 480 Mbit/s between its ports, so several DUs share a hub comfortably. The DU's hardware
decoder takes about 6-8 ms for a 768x768 picture and 25 ms for a 1920x768 band (measured, firmware 0.6.0); the
largest frame a DU takes is `max_frame` in INFO (512 KB, 1 MB in 1080p).
