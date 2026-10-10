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
- Screens of its own (Waiting for the PC, Waiting for the DMC, Not assigned, Identify, Updating firmware), in Inter
  rasterised with stb_truetype from fonts built into the image.
- Firmware update over USB into the other app slot, confirmed only once a picture was shown (or a host-requested
  restart, or a minute up with a host); otherwise the bootloader rolls back. Three unstable boots fall back to mode 0.
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

## Chip revisions v1.x and v3.x

ESP32-P4 v1.x (the NANOs GlassLink was made on, v1.3) and v3.x (the ESP32-P4NRW32X on newer NANOs) need separate
images: `sdkconfig.defaults` builds for v1.x, `sdkconfig.p4v3` on top builds for v3.x into its own folder:

```
idf.py -B build-p4v3 -D SDKCONFIG=build-p4v3/sdkconfig -D "SDKCONFIG_DEFAULTS=sdkconfig.defaults;sdkconfig.p4v3" build
```

The DMC offers `firmware/build-p4v3/glasslink_du.bin` (an installed copy: `firmware\glasslink_du-p4v3.bin`) to a DU
whose INFO says `chip_rev` 300 or more, the other image to the rest, and refuses to send an image whose header names
other revisions. The v3.x image is built in CI and released, but not yet tested on a v3.x board (#82).

## Released and own builds (signed updates)

Released firmware is built with `sdkconfig.release` on top and signed by the release workflow
(`idf.py -D SDKCONFIG_DEFAULTS="sdkconfig.defaults;sdkconfig.release" build`, then `espsecure sign_data`). A DU running
it installs an update over USB only when it is signed with the GlassLink release key, so no program on a PC can put
other firmware on it through the GlassLink link (ESP-IDF's "signed app images without hardware secure boot"). Nothing
is burnt into the chip: the USB-C serial port always takes any firmware. `"signed": 1` in INFO, "takes signed releases
only" on the status page.

A build from `sdkconfig.defaults` alone, as above, checks nothing: development builds and your own install over USB as
always, and take a release too. To put your own build on a DU that runs a release, flash it once over the USB-C port
(`idf.py -p COMx flash`, or Set up a board with your build in `firmware/build`); from then on it updates over USB
again. The release key cannot be changed over USB: it is
the key the running firmware was signed with (`firmware/signing-key.pub.pem`).

## Flash

- **First time**, over the NANO's USB-C port (CH343 serial): on the status page, Display units, **Set up a board**
  (no ESP-IDF needed: the DMC's own flasher writes the flash set of `firmware/build` or `build-p4v3`, or of an
  installed copy's `firmware\flash\v1` / `p4v3`, whichever fits the chip), or `idf.py -p COMx flash monitor`.
  Neither writes NVS (0x9000), so a DU keeps its serial and label. The flasher speaks Espressif's ROM serial protocol
  itself (`dotnet/src/GlassLink.Core/Flash`), lists only the NANO's CH343 ports and checks every part with the chip's
  MD5; 24 s for the whole set at 460800 baud on DU2.
- **After that**, from the status page (Display units, Update) or with the bench tool
  (`GlassLink.Bench update firmware/build/glasslink_du.bin --serial <prefix>`, DMC stopped). The DMC offers an update
  when a DU's firmware is older than `FIRMWARE_VERSION`; it says so up front when a DU running a release would refuse
  an unsigned image.

## Diagnostics

- Serial console without resetting the DU: `firmware/seriallog.py COMx <seconds> <file>` with the IDF Python.
- A panic address: `riscv32-esp-elf-addr2line -e firmware/build/glasslink_du.elf <address>`.
- NVS keys for bench work: `diag` (cycle DSI variants per boot), `dsivar` (0..3), `mode` (0..4);
  `display_diag_test_pattern()` shows the bridge's own test pattern.
