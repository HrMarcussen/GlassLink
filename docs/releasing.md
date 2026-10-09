# Releasing

A release is built, signed and published by GitHub Actions (`.github/workflows/release.yml`) from a version tag.
The shared steps come from [release-tools](https://github.com/HrMarcussen/release-tools).

## Making a release

1. Move the "Unreleased" part of `CHANGELOG.md` into a section `## [x.y.z] - <date>` (with a short summary line).
2. Raise `VERSION` to x.y.z, and `FIRMWARE_VERSION` too if anything under `firmware/` changed since the last release.
3. Commit, tag `vx.y.z`, push the commit and the tag.
4. Actions, Release: approve the run (the `release` environment asks). It builds the firmware, runs the tests,
   builds the DMC and the installer, signs GlassLink.exe and the installer, and publishes the GitHub Release with:
   - `GlassLink-x.y.z-setup.exe`, the installer
   - `GlassLink-x.y.z-win-x64.zip`, the same without an installer
   - `glasslink_du-x.y.z.bin`, the DU firmware (the installer contains it too)
   - `SHA256SUMS.txt`, and the changelog section as the notes
5. Installed DMCs see the release within six hours (or at once with Check now on the System tab) and offer
   **Install update**. They look at the latest *published* release: drafts and pre-releases are not offered. An
   update needs the `-setup.exe` and `SHA256SUMS.txt` assets, which the workflow always publishes.
6. Update the DUs from the status page of an updated DMC.

A **dry run** (same build, nothing published, the files kept as a workflow artifact for a week): Actions, Release,
Run workflow, on `main`. A dry run without `BUILD_DEPS_TOKEN` builds without `SimConnect.dll` and says so; a tag's
run stops instead (`build-release.ps1 -Strict`), as it does without the firmware image or Inno Setup.

## One-time setup

| What | Where | Why |
|---|---|---|
| Environment secret `BUILD_DEPS_TOKEN` in `release` | Settings, Environments, release (or Secrets and variables, Actions, Manage environment secrets) | reads `SimConnect.dll` from the private `HrMarcussen/build-deps` (fine-grained token: that repository only, Contents read-only, with an expiry date) |
| Environment `release` with a required reviewer | Settings, Environments | nothing is signed or published without an approval |
| Environment secret `FIRMWARE_SIGNING_KEY` in `release`, and `firmware/signing-key.pub.pem` committed | run `tools\new-firmware-key.ps1` once: it makes the key outside the repository and writes the public half; paste the private key file's whole text as the secret, keep a copy in a password manager, delete the file | signs the DU firmware; a DU running a release installs only updates signed with it, for good, so the key is never replaced. A tag's run stops without it; the workflow checks the signature against the public key |
| Environment secrets `CERTUM_USER`, `CERTUM_OTP_SECRET`; variable `SIGN_METHOD` = `certum-simplysign` | the `release` environment; Settings, Variables | code signing (Certum Open Source certificate, SimplySign cloud). Until then releases are unsigned and the run says so |

## SimConnect.dll

Microsoft's, never in this repository (#58). The copy releases ship is `GlassLink/SimConnect.dll` in the private
`HrMarcussen/build-deps`, with its origin and SHA-256 in that repository's README. `THIRD-PARTY-NOTICES.md` says what
it is and under which terms.

## Building a release on a PC

`tools\build-release.ps1` builds the same files locally (unsigned): needs the .NET 10 SDK, Inno Setup 6
(`winget install JRSoftware.InnoSetup`), the firmware built with ESP-IDF 5.5.5 (`firmware/README.md`), and
`build-deps` cloned next to this repository (`..\build-deps`) or `SimConnect.dll` in `dotnet\lib`.

On a new PC: install those three tools, clone this repository and `build-deps` side by side, build the firmware,
run the script. Nothing else lives only on a PC.
