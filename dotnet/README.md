# GlassLink DMC for .NET

The port of the Python DMC (`../glasslink`), layer by layer, with the Python version as the reference. Same USB
protocol (`../docs/usb-protocol.md`), same `config.json`, same HTTP API and status page. Plan and UI requirements:
`../docs/BACKLOG.md`, item 5.

| Layer | State |
|---|---|
| 1. USB and DUs: protocol, WinUSB transport, DU connection (frames, commands, health, firmware update) | done, verified on two DUs |
| 1b. DU manager: hot-plug, assignments from `config.json` | next |
| 2. Capture and encode (Windows.Graphics.Capture, change detection, JPEG) | |
| 3. Pop-out and SimConnect (profiles, camera, brightness link) | |
| 4. HTTP API + the existing status page, tray icon, start and stop with the sim | |

```
dotnet test GlassLink.slnx
dotnet run --project src/GlassLink.Bench -- list
dotnet run --project src/GlassLink.Bench -- stream --seconds 20 --fps 30
dotnet run --project src/GlassLink.Bench -- update ../firmware/build/glasslink_du.bin --serial ff69
```

The bench tool needs the DUs for itself: stop the Python DMC first (`python tools/stop_server.py`).

Design notes
- **WinUSB directly** (P/Invoke, no libusb): the DU binds to Windows' own driver through its MS OS 2.0 descriptors.
  One reader thread per DU keeps a read pending and never cancels it; a bulk read that is cancelled by a timeout can
  lose the bytes that arrived at that moment. Writes end with a zero-length packet when needed (pipe policy).
- **The DU is the clock**: one frame in flight, always the newest (`IFrameSource.WaitNewer` is a real predicate wait).
  While a READY is pending the connection waits for the next picture, not for the USB read, so a picture that
  appears after a still phase goes out at once (the Python version can sit in its 250 ms read first).
- Measured 21 Sept 2026, two DUs on one hub, 768x768 test pictures: 30.0 fps to each at 3.4 % of one core and
  52 MB; ceiling 41-43 fps per DU (decode 6 + draw 12.5 + transfer 3.5 ms); ping 0.4-0.9 ms; firmware update
  445 KB in 3.5 s.
