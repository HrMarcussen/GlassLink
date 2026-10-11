# GlassLink DMC <-> DU USB protocol (draft 1, 2026-09-08)

Applies to the ESP32-P4 display modules and to the DMC's USB output path (`dotnet/`; the original Python DMC was
retired in 0.8.0).
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
| 0x04 | SET_ROTATION | none | 0, 90, 180, 270 | Rotation of the picture. Stored in NVS (`rotation`), not applied yet: the panels are mounted upright. |
| 0x05 | SHOW_IDENT | UTF-8 label of the module (<= 31 bytes, may be empty) | seconds, 0 = off | Stamp an "IDENT <label>" banner with the serial across the top of every frame (live picture or the kept last frame) so the user can see which physical unit this is while assigning. 0 cancels it; the module then redraws the last frame without the banner. |
| 0x06 | PING | none | nonce | Module answers PONG with the same nonce. |
| 0x07 | SET_ASSIGNED | optional (0.7): the DU's label, a newline, the assigned display's name, UTF-8 (`DU1\nCaptain PFD`) | 0 = nothing assigned, 1 = assigned and its pictures come, 2 (0.7) = assigned, but its display has no window (the sim is not showing it) | Sent after INFO, whenever the assignment changes, and (0.7) every 2 s, which also tells the DU that a DMC is there: with no message from a host for 6 s the DU shows "Waiting for the DMC". 0 shows "Not assigned", 2 "<display> · waiting for the sim" instead of an old picture; the DU keeps the label in NVS for its screens. Older firmware reads only arg 0 / not 0. |
| 0x09 | SET_MODE | none | HDMI mode: 0 = 768x768, 1 = 1024x768, 2 = 800x600, 3 = 1280x720, 4 = 1920x1080 at 30 Hz (two DSI lanes carry no more; one frame buffer) | The DU stores the mode and restarts into it (about 3 s); INFO reports it as `mode`. For a DU on an ordinary HDMI screen. A frame smaller than the screen is centred, a larger one refused. |
| 0x0A | SET_LAYOUT | tiles, 8 bytes each: uint16 LE x, y, w, h (up to 6) | bit 0 = show test cards | A screen with several displays on it: the DU keeps the layout in RAM (the host sends it after every INFO), clears the screen and then takes TILE messages. With bit 0 set every tile is drawn as a test card (bright border, number, size) for lining the tiles up with a panel's cutouts; TILEs are kept but not drawn meanwhile. An empty payload ends tile mode. INFO reports `tiles` (the count) and `caps` containing `tiles`. Tile sizes (and so the displays' `client_size`) are multiples of 16: the DU's hardware JPEG decoder writes whole 16 x 16 blocks of a 4:2:0 picture and refuses other sizes (found 22 Sept 2026, #15); positions are free. A DU whose `caps` contain `band` (0.6.0) decodes a picture as wide as its screen straight into the frame buffer, so the DMC sends all tiles of such a DU as one layout tile covering the rows they use, full width, with the displays side by side on black (a "band"); test cards stay per tile. |
| 0x0B | TILE | JPEG | tile index | A frame for one tile of the layout, centred in its rectangle; the rest of the screen is untouched. Same flow control as FRAME (one in flight, READY after it). A tile costs what a frame of its own size costs, whatever the screen size: two 640x640 tiles on a 1280x720 screen run at 48 tiles/s in total, four 432x432 at 77/s (measured 22 Sept 2026). |
| 0x0C | BYE | none | 0 | (0.7) The DMC is quitting: the DU shows "Waiting for the DMC" at once and forgets the pictures of the session. Older firmware skips it as unknown. |
| 0x10 | OTA_BEGIN | none | total image size | Start a firmware update. The DU erases the inactive slot, shows an "UPDATING FIRMWARE" banner, ignores FRAMEs, and answers OTA_PROGRESS 0 (or OTA_RESULT 1). |
| 0x11 | OTA_DATA | firmware chunk (the DMC uses 32 KiB) | offset of this chunk | Must arrive in order. Answered with OTA_PROGRESS = bytes written so far; the host sends the next chunk only then (stop and wait, because flash writes block the DU). |
| 0x12 | OTA_END | none | CRC32 of the image (zlib) | The DU checks size and CRC, lets ESP-IDF validate the image, selects the new slot, answers OTA_RESULT and reboots if it was 0. The new image confirms itself once it has shown a picture or tile, the host started an update or asked for a restart, or it ran a minute with a host; a restart before that rolls back. |
| 0x20 | REBOOT | none | 0 | |

## 4. Module -> host (bulk IN)

| type | name | payload | arg | meaning |
|---|---|---|---|---|
| 0x81 | READY | none | last displayed seq | "Send me the newest frame." Sent once at start, after every FRAME has been decoded and shown, after every INFO reply, and repeated every 2 s while idle (so a host that connects later, or missed a READY, still starts). This is the flow control: the host never has more than one frame in flight, and always sends the newest one (same latest-only rule as the WebSocket path). |
| 0x82 | INFO | JSON: `{"fw":"0.9.0","build":"62843c4","hw":"p4-nano+lt8912b","panel":[768,768],"decoder":"hw","uptime_s":1234,"serial":"a1b2c3d4e5f60718293a4b5c","mode":0,"ident":0,"caps":["mode","tiles","band"],"tiles":0,"max_frame":524288,"max_tiles":6,"slot":"ota_0","confirmed":1,"signed":0,"chip_rev":103,"mac":"30eda0e12345","display_error":""}` | 0 | `max_frame` depends on the mode. `slot` is the app slot it runs from, `confirmed` 0 while a new image can still roll back. `signed` 1: released firmware, which installs only updates signed with the release key (OTA_RESULT 3 otherwise; firmware/README.md). `chip_rev` (0.11): the ESP32-P4 revision, major * 100 + minor; v1.x and v3.x chips need different images (#82). `mac` (0.11): the chip's factory MAC, by which the DMC's board setup knows the DU in its bootloader. |
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
   is stored but not applied yet: the panels are mounted upright.
5. OTA: dual app partitions; the new image is written to the inactive slot, verified, and booted with rollback protection.

## 6a. Panel power and backlight (integrated DU, #66, #67; rewritten 11 Oct 2026, not implemented yet)

For a DU whose NANO sits on a carrier board with the panel's scaler (one case, one 12 V supply per DU): the carrier
switches the scaler's 12 V and feeds a PWM into the scaler's VR_ADC input. Protocol version stays 1. A DMC sends
these messages only to a DU whose `caps` name them: older firmware takes an unknown type as a broken header.

Hardware (carrier concept v2, NANO; the FireBeetle variant may move pins): GPIO21 enables the scaler's 12 V switch,
with a pull-down, so the panel is unpowered while the P4 is in reset, booting, crashed or running firmware that does
not know the switch (fail dark). GPIO20 = PWM (LEDC, about 20 kHz) into VR_ADC. GPIO23 = carrier strap: low means
a carrier is there (read with the internal pull-up). Firmware drives GPIO20/21 only when GPIO23 says carrier, so a
plain NANO never gets an unexpected output; NVS `carrier` overrides that on the bench.

- `0x08 SET_PANEL_POWER` (host -> DU), arg 1 = on, 0 = off. A **state**, not an event: the DMC sends it after INFO
  and with every `SET_ASSIGNED` heartbeat (2 s), so a DU that restarts has it back within 2 s. It counts only while
  a sim is connected: then the panel follows the aircraft, off when the profile's "DU off" rule says that DU is dark
  in the cockpit (its knob at OFF, or the aircraft unpowered: cold and dark) for about 1 s, and off from the moment
  the sim connects until the rule knows the aircraft's state. Otherwise the DMC says on: no sim (the DU shows
  "<display> · waiting for the sim" or "Not assigned"), and an aircraft without a rule (nothing could ever turn it
  on).
- What the DU does: a black DU is never a mystery, so it powers the panel for its own status screens (Thomas, 11
  Oct 2026, through the Electronics session):

  | Situation | Panel |
  |---|---|
  | Power-up, no USB host yet (PC off or booting, cable missing) | on with "No USB" for 2 minutes, then off |
  | USB host lost without a `BYE` (cable pulled, PC crashed) | on with "No USB" for 2 minutes, then off |
  | `BYE` (the DMC quits, a clean PC shutdown) | off at once, until a host or DMC speaks again |
  | USB host there, no DMC messages | on: "Waiting for the DMC" |
  | DMC there | as its `SET_PANEL_POWER` says; on with a DMC that never sends it |

  The DU cannot tell a PC that is off from a pulled cable (both are "no host", and VBUS does not tell either, bench
  test B8), so "No USB" shows for a limited time; the 2 minutes also cover a PC booting after the cockpit's mains
  comes on.
  - Off: a black picture and the backlight to its minimum first, then the 12 V off about 100 ms later (the nearest
    to the panel's own power-down order). On: the 12 V on; pictures simply keep coming (the panel shows them about
    2 s later). The DU keeps taking and drawing pictures while off, so the right one is there when it comes on.
- `0x0D SET_DIMMING` (host -> DU), arg 0 = pixels (the default at every boot), 1 = backlight. Sent after INFO and when
  it changes, only to a DU whose `caps` contain `backlight`. The DMC sends 1 only for an aircraft whose profile asks
  for it (tested profiles only; GlassLink's default stays pixel dimming). With 1, `SET_BRIGHTNESS` drives the PWM;
  below the backlight's minimum (this scaler's backlight 0 is dim, not dark) the DU dims the pixels as well. Whether
  VR_ADC drives the backlight at all is still to be proven on the bench (test B1), and the P4 cannot see whether the
  VR lead is plugged in, so pixel dimming must stay the fallback.
- INFO and STATS on such a DU: `"panel_power": "on" | "off"`, `"panel_why"`: `"no_usb"`, `"no_usb_timeout"`, `"bye"`,
  `"host_no_dmc"`, `"dmc_on"` or `"dmc_off"` (so the status page can say why a DU is dark), `"panel_seen": true |
  false` (the scaler answers on I2C, EDID at
  0x50: a check of the switch, given 5 s after switching on; it shows power, not a lit picture, since the scaler's
  own soft-off still answers), `"dimming": "pixels" | "backlight"`, and `caps` with `panel_power` and `backlight`.
  The keys are absent on a DU without a carrier.

## 7. Sizes and rates

A 768x768 JPEG at quality 85 is 30 to 60 KB for a Fenix display, up to about 140 KB for a busy picture; a 1080p band
of two displays 100 to 300 KB. At 30 fps that is 1 to 9 MB/s per DU; a DU receives about 28 MB/s (16 KB transfers),
and a USB 2.0 hub shares 480 Mbit/s between its ports, so several DUs share a hub comfortably. The DU's hardware
decoder takes about 6-8 ms for a 768x768 picture and 25 ms for a 1920x768 band (measured, firmware 0.6.0); the
largest frame a DU takes is `max_frame` in INFO (512 KB, 1 MB in 1080p).
