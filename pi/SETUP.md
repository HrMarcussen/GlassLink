# Raspberry Pi 5 setup (next step after the PoC)

Two ways to show a stream on a Pi with its own panel. Both only need the server running on the sim PC.

## Option A – Chromium kiosk (simplest)

1. Raspberry Pi OS (64-bit, desktop). Set the panel resolution/rotation in `raspi-config` or `/boot/firmware/cmdline.txt` (`video=HDMI-A-1:1024x768@60,rotate=90` style) as needed.
2. Autostart Chromium in kiosk mode. Create `~/.config/autostart/glasslink.desktop`:

   ```
   [Desktop Entry]
   Type=Application
   Name=GlassLink
   Exec=chromium-browser --kiosk --noerrdialogs --disable-infobars --incognito --autoplay-policy=no-user-gesture-required http://192.168.1.10:8765/view/pfd
   ```

   Add `?rotate=90` to the URL if the panel is mounted rotated and you prefer to rotate in the page.
3. Hide the cursor / screen blanking: `sudo apt install unclutter` and disable DPMS (`xset s off -dpms` in the same autostart, or the Wayland equivalent in `raspi-config`).

## Option B – Native viewer (no desktop, lower overhead)

1. Raspberry Pi OS Lite (64-bit).
2. `sudo apt install python3-pygame python3-websockets python3-simplejpeg` (or `pip install websockets simplejpeg pygame` in a venv).
3. Copy `viewer.py` to `/home/pi/glasslink/`, test with:

   ```
   SDL_VIDEODRIVER=kmsdrm python3 viewer.py --url ws://192.168.1.10:8765/ws/pfd --fps
   ```

4. Install `glasslink-viewer.service` to `/etc/systemd/system/`, edit the URL/rotation, then
   `sudo systemctl enable --now glasslink-viewer`.

## Single USB cable (USB gadget Ethernet)

The Pi 5 (and Pi 4 / Zero 2 W) USB-C port can act as a USB Ethernet device, so one cable from the sim PC carries power and an IP link.

1. In `/boot/firmware/config.txt` add `dtoverlay=dwc2,dr_mode=peripheral`.
2. In `/boot/firmware/cmdline.txt` append `modules-load=dwc2,g_ether` (after `rootwait`).
3. Give the `usb0` interface a static IP (e.g. `192.168.7.2/24`) via `/etc/network/interfaces.d/usb0` or NetworkManager, and give the sim PC's new "USB Ethernet/RNDIS Gadget" adapter `192.168.7.1/24`.
4. Windows 11 needs the RNDIS driver: if the adapter shows as unknown, use the `libcomposite` ECM/NCM gadget instead (works with the inbox Windows 11 NCM driver), or install the Microsoft RNDIS driver via Device Manager > Update driver > "Network adapters" > Microsoft > "Remote NDIS Compatible Device".
5. Point the viewer at `ws://192.168.7.1:8765/ws/pfd`.

Note: a Pi 5 powered from a PC USB port is limited to ~900 mA on most ports; a 5-7" panel plus Pi 5 may need a powered hub or separate power. The Pi Zero 2 W / Pi 4 draw less.

## Panel choice

Airbus DUs are square-ish (7.25" active area). Common choices: 8" 1024x768 or 1024x1024 IPS HDMI panels; the viewer letterboxes any aspect ratio. Set `client_size` in `config.json` on the sim PC to the panel's native resolution so the sim renders the pop-out at exactly that size.
