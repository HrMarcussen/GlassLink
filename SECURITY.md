# Security

Please report a vulnerability privately: on GitHub, the repository's **Security** tab, **Report a vulnerability**.
Do not open a public issue for it.

What matters most here:

- **The DMC's HTTP server** (port 8765): the status page and API on the sim PC. Changes are accepted only from the PC
  itself unless `server.allow_lan_control` is set, only as JSON, and only for this PC's own host names (against
  cross-site requests and DNS rebinding). A way around any of that is a vulnerability.
- **Firmware updates over USB**: a DU running released firmware installs only images signed with the GlassLink release
  key (`firmware/signing-key.pub.pem`; the private key lives in the release workflow's environment and offline). A DU
  running a development or self-built firmware installs anything. An installed DMC takes only the image installed with
  it (and its version from the install folder), never one named in its configuration folder, which any program the
  user runs can write to. Flashing over a DU's USB-C serial port needs physical access and is not restricted.
- **Releases and updates**: installers are published on GitHub Releases with SHA-256 checksums (code signing is being
  set up). The DMC installs an update only on the user's click, only if its SHA-256 matches the release's
  SHA256SUMS.txt, and, when GlassLink itself is signed, only if the update is validly signed by the same publisher. A
  way around that is a vulnerability.

Only the latest release gets fixes.
