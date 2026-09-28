# GlassLink DU firmware (ESP32-P4)

Firmware for a DU: Waveshare ESP32-P4-NANO + Olimex MIPI-HDMI adapter (LT8912B), feeding an HDMI monitor or a
panel's scaler board ([docs/hardware.md](../docs/hardware.md)). Protocol: [docs/usb-protocol.md](../docs/usb-protocol.md).
Why things are done the way they are: [docs/notes.md](../docs/notes.md).

## What it does

- Enumerates on the P4's high-speed USB port as vendor device `303A:4001` "GlassLink DU" with Microsoft OS 2.0
  descriptors, so Windows binds WinUSB without a driver. The USB serial is the DU's identity (24 hex digits, made
  once from the chip's MAC and kept in NVS).
- Drives the LT8912B over I2C and MIPI-DSI (2 lanes) in one of five HDMI modes, chosen by the DMC (`SET_MODE`, kept
  in NVS `mode`): 0 = 768x768@60 (default), 1 = 1024x768@60, 2 = 800x600@60, 3 = 1280x720@60, 4 = 1920x1080@30.
- Receives JPEGs over USB and decodes them with the hardware decoder, while the next one is already coming in.
  Full-size pictures go straight into the back frame buffer (flip, no tearing), pictures as wide as the screen
  straight into their rows, others are copied into place. Brightness is applied in the decoder's colour conversion.
- Layouts of up to six tiles (`SET_LAYOUT`, `TILE`) with test cards for lining them up with a panel's cut-outs.
- Screens of its own, without a font library: NO USB, NOT ASSIGNED with the serial, IDENT with the label.
- Firmware update over USB into the other app slot, confirmed only once a picture was shown (or a host-requested
  restart, or a minute up); otherwise the bootloader rolls back. Three unstable boots fall back to mode 0.
- Stats every 2 s (fps, decode, draw and transfer times, free PSRAM), log lines to the host.

## Build

ESP-IDF 5.5.5 from the Espressif Windows installer (`C:\Espressif`). From PowerShell:

```
$env:PATH = "C:\Espressif\tools\idf-python\3.11.2;" + $env:PATH
$env:IDF_TOOLS_PATH = "C:\Espressif"; $env:IDF_PATH = "C:\Espressif\frameworks\esp-idf-v5.5.5"
. "$env:IDF_PATH\export.ps1"
cd firmware
idf.py build
```

(or the "ESP-IDF 5.5 CMD" shortcut). The first build downloads the managed components (`espressif/esp_tinyusb`,
`espressif/esp_lcd_lt8912b`). The version comes from the repository's `VERSION` file, the build id from
`git describe`. CI builds every push that touches `firmware/`.

## Flash

- **First time**, over the NANO's USB-C port (CH343 serial): `idf.py -p COMx flash monitor`.
- **After that**, from the status page (Display units, Update) or with the bench tool
  (`GlassLink.Bench update firmware/build/glasslink_du.bin --serial <prefix>`, DMC stopped). The DMC offers an update
  when a DU's firmware is older than `FIRMWARE_VERSION`.

## Diagnostics

- Serial console without resetting the DU: `firmware/seriallog.py COMx <seconds> <file>` with the IDF Python.
- A panic address: `riscv32-esp-elf-addr2line -e firmware/build/glasslink_du.elf <address>`.
- NVS keys for bench work: `diag` (cycle DSI variants per boot), `dsivar` (0..3), `mode` (0..4);
  `display_diag_test_pattern()` shows the bridge's own test pattern.
