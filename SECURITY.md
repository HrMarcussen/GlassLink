# Security

Please report a vulnerability privately: on GitHub, the repository's **Security** tab, **Report a vulnerability**.
Do not open a public issue for it.

What matters most here:

- **The DMC's HTTP server** (port 8765): the status page and API on the sim PC. Changes are accepted only from the PC
  itself unless `server.allow_lan_control` is set, only as JSON, and only for this PC's own host names (against
  cross-site requests and DNS rebinding). A way around any of that is a vulnerability.
- **Firmware updates over USB**: a DU installs what the DMC sends it. The DMC takes the image that ships with it.
- **Releases**: installers are published on GitHub Releases with SHA-256 checksums (code signing is being set up).

Only the latest release gets fixes.
