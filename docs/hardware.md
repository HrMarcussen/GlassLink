# DU hardware

A DU today is three off-the-shelf boards and a screen. The firmware is in `firmware/`, the link to the sim PC is
USB 2.0 high speed (`docs/usb-protocol.md`).

## Parts per DU

| Part | Notes | Approx. price (EU, Sept 2026) |
|---|---|---|
| Waveshare **ESP32-P4-NANO** (32 MB PSRAM, hardware JPEG decoder, 2-lane MIPI-DSI on a 15-pin FPC, USB 2.0 HS) | BerryBase, Botland, Kiwi | EUR 22 |
| Olimex **MIPI-HDMI** adapter (Lontium LT8912B) + 15-pin FPC cable | olimex.com | EUR 17 |
| HDMI cable, short | | EUR 4 |
| The screen: an HDMI monitor, or a panel with an HDMI scaler board | e.g. the Aptus DBC088HXN60L050A 8.8" 768x768 with its AD768L60V01 / HDP802 scaler board (12 V, about 5 W) | |
| 12 V -> 5 V buck (Mini560 / LM2596 type), only for a panel whose 12 V supply also feeds the NANO | | EUR 2 |

Panel details: [panel-DBC088HXN60L050A.md](panel-DBC088HXN60L050A.md).

## Wiring on the bench

- **NANO USB-C**: 5 V power, and flashing / serial console (CH343). The NANO cannot be powered through its Type-A
  socket (measured).
- **NANO Type-A socket**: the P4's high-speed USB port, the link to the sim PC. It also puts out 5 V, so use an
  A-to-A cable with the 5 V wire cut, to a USB 2.0 hub port. Several DUs share a hub without losing frames.
- DSI flat cable to the Olimex adapter (it carries the LT8912B's I2C on GPIO7/GPIO8), HDMI to the screen.
- Scaler board of the Aptus panel: set its OSD brightness to **50**. The default 100 lifts the black level and shows
  a bright ring on dark pictures.

## Facts for a packaged DU

The HDP802 V02 scaler board has, besides the barrel jack:

- an unpopulated 3-pin footprint **VR_VCC / GND / VR_ADC** on the back, next to the KEY_ADC1 keypad pads: a
  potentiometer input for the backlight. A P4 PWM pin through an RC filter (about 10 k / 10 uF) can drive it; measure
  VR_VCC first (3.3 V direct, 5 V through a divider or transistor stage).
- a 4-pin header **DC12V_IN / GND / GND / DC12V_INC_OFF**: power input with a power-off line, so a DU could really
  switch off with the aircraft's DU power instead of showing a backlit black picture. Idle level and polarity are
  still to be measured.

The protocol already reserves both (`docs/usb-protocol.md`, section 6a): `SET_BRIGHTNESS` becomes the backlight when
a DU reports `backlight`, `SET_PANEL_POWER` (0x08) the power line.

A data-only USB-C socket for an enclosure: panel-mount USB-C in device role (2 x 5.1 k pull-downs on CC), D+/D-/GND
to the NANO's Type-A pins, VBUS not connected, so a standard C-to-A cable works.

## Platform choice

Decided 19 Sept 2026 to stay on the ESP32-P4. A Linux board with native HDMI (Raspberry Pi Zero 2 W, Radxa Zero 3W)
would save the HDMI bridge, but the P4 shows a picture two seconds after power and does not mind the power being cut
at the wall every evening (no OS, no SD card to corrupt), and it keeps the way open to one integrated board. Reopen
only if the P4 or the bridge becomes unobtainable. A classic ESP32 or ESP32-S3 cannot do it: no hardware JPEG
decoder and too little memory bandwidth for 768x768.

The next hardware steps (backlight and power line, an adapter board without loose wires, an integrated board with a
DSI-to-LVDS bridge) are tracked as GitHub issues with the label `area: hardware`.
