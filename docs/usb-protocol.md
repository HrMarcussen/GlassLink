# GlassLink DMC <-> DU USB protocol (draft 1, 2026-09-08)

Applies to the ESP32-P4 display modules and to the server's USB output path (Python now, .NET later).
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
A message whose total size is a multiple of 512 is followed by a zero-length packet (ZLP) by the sender.

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
| 0x07 | SET_ASSIGNED | none | 1 = assigned, 0 = not | Sent after INFO and whenever the assignment changes. With 0 the module shows its NOT ASSIGNED screen instead of the last frame. |
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
2. Send GET_INFO; the module's panel size is compared with the display's `client_size` (they should match; if not, the host downscales or the module letterboxes).
3. Look up the serial in `modules`. Unassigned modules are listed in the app with a "show ident" button; assigning writes the config.
   Send SET_ASSIGNED so the module shows either the picture or its NOT ASSIGNED screen. An unplugged module stays
   listed only if it has an assignment or a label ("Forget" removes that); an unconfigured unit vanishes when unplugged.
4. Loop: wait for READY, then send the newest JPEG of the assigned display if its seq is newer than the READY's seq, otherwise wait for a new frame. This is exactly the WebSocket hub logic with a different transport.
5. On USB error or unplug: close, forget, and pick the device up again on the next enumeration.

## 6. Module behaviour

1. Boot, init panel via LT8912B (768x768@60, 2 DSI lanes), show the "not assigned" screen with serial and firmware version.
2. Start USB; on configuration, send READY.
3. On FRAME: hardware-JPEG-decode straight into the back buffer, swap on vsync, send READY with the frame's seq.
   Decode errors are reported with LOG and answered with READY so the stream continues.
   The module keeps a copy of the last frame and redraws it when an overlay (IDENT, idle screens) ends.
   Stream resync: if a header does not validate (magic, version, known type, length), the module slides its
   16-byte header window one byte at a time over the incoming data until one does; this recovers from a host that
   was restarted in the middle of a message.
4. If no FRAME arrives for 5 s, show a small "no signal" marker in a corner (the last frame stays on screen).
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

768x768 JPEG at quality 85 is 30 to 60 KB. At 30 fps that is about 1.5 MB/s per module; a high-speed USB
link carries 30 to 40 MB/s, a hub shares 480 Mbit/s between its ports, so eight modules on two hubs use about
a third of one hub's bandwidth. Hardware JPEG decode of 768x768 on the P4 is expected to take 10 to 15 ms.
