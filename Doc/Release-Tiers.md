# Release-tier contract

What each published artifact PROMISES to contain, and the executable gates that enforce it.
Platform policy: **Linux is primary, Windows is supported, macOS is not currently supported**
(resolver branches are best-effort portability code, not a tested promise).

## Tiers (linux-x64)

| Tier | Contents | Intended host |
|---|---|---|
| **full** (`HaPlay.Desktop-linux-x64-full`) | Every supported native the app can load: Skia/HarfBuzz, SDL3, FFmpeg 8.1, PortAudio, PortMidi, miniaudio, libass 0.17.5, projectM 4.1.6 (+ preset/texture packs under `External/projectm/`), Bullet shim. | Any Linux host; the validated release deliverable. |
| **core** | Only the render essentials distros can't supply (libSkiaSharp, libHarfBuzzSharp) + the Bullet shim. Everything else from the host. | Hosts with a maintained media stack. |
| **minimal** | Only libmmd_bullet.so (no upstream package exists). | Fully host-provisioned installs; see the bundle's `DEPENDENCIES.txt`. |

Windows publishes a single full-equivalent artifact.

**NDI is the named legal exception:** its runtime is proprietary and not redistributable in these
artifacts — it must be installed on the host, and the feature degrades gracefully without it.

## Gates a release must pass (enforced in `.github/workflows/build.yml`)

1. Solution build with warnings-as-errors; full test suite with real project serialization
   (`-m:1`), TRX + blame output preserved, and a loud "passed only on retry" annotation for flakes.
2. `scripts/check-native-manifest.sh` — every promised native file is present (REL-01).
3. `scripts/load-probe-native-manifest.sh` — every promised native actually loads on the artifact.
4. `scripts/check-native-versions.sh` — version claims are executable: libass reports >= 0.17.5,
   miniaudio exactly 0.11.25, projectM exactly 4.1.6 from the staged tree.
5. Native SBOM (`native-provenance.txt`) written into the artifact: project, pinned version,
   source URL, license, SHA-256 of each shipped file.
6. The exact upload directory launches under Xvfb, reaches backend enumeration
   (`MediaRuntime ready`) and renders a first frame (`HAPLAY_SMOKE`), then exits cleanly.

The reduced tiers deliberately omit "required" natives, so only the **full** tier runs gates 2–6.
