# GlassLink DMC (.NET)

`GlassLink.exe`, the DMC: a tray application with the status page in a window of its own and on port 8765. .NET 10,
Windows only. The Python DMC in `../glasslink` was the original and stays as reference and test tool; both speak the
same USB protocol, read the same `config.json` and serve the same status page (`glasslink/static/*.html`, copied at
build time; `engine` in `/status` says which DMC answers).

## Projects

| Project | |
|---|---|
| `GlassLink.Core` | USB protocol and WinUSB transport, DU connection (frames, tiles, commands, health, firmware update), DU manager (hot-plug, assignments, layouts, bands), configuration file |
| `GlassLink.Capture` | window finder, Windows.Graphics.Capture, change detection, JPEG (libjpeg-turbo), downscale, band composer |
| `GlassLink.Sim` | SimConnect client, aircraft profiles, pop-out procedure and automatic pop-out, Learn, brightness link |
| `GlassLink.Dmc` | the program: HTTP API, status window (WebView2), tray, advice, display editor, firmware updates, start with Windows / with the sim |
| `GlassLink.Bench` | the bench tool for DUs without the DMC |
| `tests/GlassLink.Core.Tests` | xUnit, with an in-process fake DU |

## Build, run, test

```
dotnet build GlassLink.slnx -c Release
src/GlassLink.Dmc/bin/Release/net10.0-windows10.0.26100.0/GlassLink.exe     # or ..\start-server.bat
dotnet test GlassLink.slnx
```

Only one DMC runs at a time (a named mutex). `GlassLink.exe --quit` stops the running one gracefully; starting it
while it runs opens the status window. Its log is `logs/dmc-<date>.log` next to `config.json`.

**SimConnect.dll** is Microsoft's and not in the repository; releases include it unmodified (see
`THIRD-PARTY-NOTICES.md`). At run time it is looked for next to `GlassLink.exe`, in `%LOCALAPPDATA%\GlassLink\`, and
in the MSFS SDK (`MSFS2024_SDK`, `MSFS_SDK`). Put a copy in `dotnet/lib/` (ignored by git): the build puts it next to
the exe and `tools\build-release.ps1` into the release.

## Bench tool

Talks to the DUs directly; stop the DMC first (the DUs can be opened by one program at a time).

```
dotnet run -c Release --project src/GlassLink.Bench -- list
dotnet run -c Release --project src/GlassLink.Bench -- stream --size 768x768 --fps 60 [--busy] [--444] [--brightness 40] [--only <serial>]
dotnet run -c Release --project src/GlassLink.Bench -- tiles --serial <prefix> --layout "40,156,768,768;1100,156,768,768" [--cards]
dotnet run -c Release --project src/GlassLink.Bench -- mode 4 --serial <prefix>
dotnet run -c Release --project src/GlassLink.Bench -- manage --config <file>          # the DU manager with an assignment file
dotnet run -c Release --project src/GlassLink.Bench -- run --config <file> [--sim]     # a DMC in miniature
dotnet run -c Release --project src/GlassLink.Bench -- update ../firmware/build/glasslink_du.bin --serial <prefix>
```

`--busy` draws PFD-like pictures (about 140 KB), the plain test picture is about 30 KB.

## Design notes

- **WinUSB directly** through P/Invoke, no libusb. One reader thread per DU keeps a read pending and never cancels
  it (a cancelled bulk read can lose the bytes that arrived at that moment). Writes end with a zero-length packet
  when their length is a multiple of 512 (pipe policy); the DU relies on that.
- **The DU is the clock**: one picture in flight, always the newest (`IFrameSource.WaitNewer` is a real predicate
  wait). A picture chosen for a layout is sent only if that layout is still the current one.
- **Capture on demand**: a display that no DU, band or viewer uses is refused before any GPU copy.
- **Configuration** is kept as a JSON tree, so keys this program does not know (the Python DMC's) survive a save;
  saves go through a temporary file and keep a `.bak`.
- **Requests**: changes are accepted only from this PC unless `server.allow_lan_control` is set, only as JSON, and
  only with this PC's host name or address (against DNS rebinding and cross-site requests).
- **UI**: light and dark follow Windows, text follows the system text size, no state is shown by colour alone.
  The tray icon is a disc with a tick, an exclamation mark or a cross.
- More findings: [docs/notes.md](../docs/notes.md).

## Release build and installer

`tools\build-release.ps1` publishes one self-contained `GlassLink.exe` (no .NET runtime needed) with the status
page, `config.example.json`, the DU firmware image from `firmware\build` and a `BUILD` file, into
`dist\GlassLink-<version>\` and a zip, and with Inno Setup 6 installed (`winget install JRSoftware.InnoSetup`) also
`dist\GlassLink-<version>-setup.exe` from `installer\GlassLink.iss`: Program Files (asks for administrator rights once), Start menu entry, optional
start at sign-in and an optional firewall rule for the status page from other devices. A running DMC is stopped
gracefully before its files are replaced; uninstalling also removes the sim's start entry and the sign-in entry.

An installed copy uses `%LOCALAPPDATA%\GlassLink\config.json` (made from `config.example.json` on the first start,
logs next to it) and the firmware image next to the exe. A checkout uses the `config.json` in the repository root.
