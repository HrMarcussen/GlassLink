# Contributing

Thanks for wanting to help. Bug reports, measurements with other hardware or aircraft, and pull requests are all
welcome.

- **Licence.** GlassLink is GPL-3.0-or-later (see the README). By contributing you agree that your contribution is
  published under the same licence.
- **Before a larger change**, open an issue to talk it through: the USB protocol (`docs/usb-protocol.md`) is shared
  by the DMC and the firmware, and a change on one side needs the other.
- **Build and test** as described in `dotnet/README.md` and `firmware/README.md`; `dotnet test dotnet/GlassLink.slnx`
  must pass. Every push and pull request is built and tested by GitHub Actions (.NET, Python, firmware).
- **User-visible changes** get an entry under "Unreleased" in `CHANGELOG.md`; changes that need the DUs reflashed are
  marked **[DU firmware]**.
- **The status page and all other UI**: follow the Windows light/dark setting and the system text size, and never show
  a state by colour alone (always text or a symbol as well).
- **Dependencies** must be GPL-3-compatible and be added to `THIRD-PARTY-NOTICES.md`. Never commit Microsoft's
  `SimConnect.dll` or other third-party binaries; releases bring what they need.
- **Measurements beat opinions**: for performance changes, say what was measured, on which hardware, before and after.
