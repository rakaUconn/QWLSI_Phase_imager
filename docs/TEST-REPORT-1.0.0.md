# Test report — QWLSI single-frame wavefront 1.0.0 (2026-10-07)

## 1. Automated suite (xUnit, `src/Qwlsi.Tests`)
**94 tests, 94 passed, 0 failed** (3 min 58 s, including the 60-second live-simulator soak, `QWLSI_LONG=1`).
Covers: golden OPD / intensity / complex field vs Python on 7 frames; lens powers; calibration from a flat frame and JSON round trip; with-reference mode; system-wavefront subtraction; OSA/ANSI Zernike basis, orientation flags, removal and coefficient-by-coefficient comparison with Python on all 7 frames; every export format (read back with numpy / scipy / tifffile); batch, cancel and error handling; auto-find order and settings round trip; wavelength behaviour; simulator source and live transmittance; 3-D mesh preparation.

First run of this release cycle: 93/94. The one failure (`Session_without_reference_matches_golden_and_reports_peak_memory`) was a test flaw, not an app bug: it capped the *whole test host* at 2500 MB while the host already held ≈ 4.8 GB of other tests' cached data (one frame in isolation peaks at ≈ 380 MB). The assertion now measures the increase caused by one frame (limit 1000 MB). Re-run: 94/94.

The 3-D viewer needs a window and has no unit test; it is covered by the scripted UI smoke test below.

## 2. Clean-location tests of the final artifacts
No clean VM or second PC was available. Instead the packages were tested on the development PC in a **fresh folder (`C:\QwlsiCleanTest`) with a stripped environment**: `PATH = C:\Windows\System32;C:\Windows` only (no dotnet, python, Intel or developer tools), no `DOTNET_*` variables, a private settings/log folder, and the .NET runtime that is bundled with the app (the About box reports **.NET 8.0.12 from the app folder**, while the PC has 8.0.18 installed — i.e. the bundled runtime was used). The GPU (NVIDIA Quadro) and the user profile were those of the dev PC, so this is **not equivalent to a PC without any dev software or GPU**.

Each run executed the app's scripted smoke test (`--selftest`) from the examples package: batch of 2 frames without reference → batch of 2 frames with reference (reference frame detected, carriers from the reference) → export (matrix txt, intensity, XYZ, npy, mat, TIFF32, PNG, sidecar JSON) → Zernike filter (piston+tilt, then + defocus + astigmatism) with filtered/raw/coefficient exports → 3-D view (hardware Direct3D, then software WARP and back, with real mouse clicks / drags / wheel after every state change) → About box → crash-report writer → help.

| Check | Portable zip | Installer |
|---|---|---|
| Install | unzip | silent per-user install, exit 0, **21 s**, Start-menu + desktop shortcut created, uninstall entry created |
| Start / run | OK | OK |
| Smoke test | exit code 0, 103 s | exit code 0, 112 s |
| Exceptions in the 3-D view (empty viewport + 5 state changes with mouse input) | 0 | 0 |
| Results | identical in both | identical in both |
| Uninstall | n/a | exit 0; folder, both shortcuts and registry entry removed (settings/logs in `%APPDATA%` kept by design) |

**Results** (identical to Python): concave_1 +0.711 D, astig 34.5 nm, rms 1863.6 nm, PV 7.432 µm; convex_2 −1.475 D, 229.9 nm, 3789.0 nm, 13.961 µm; with reference (ref.tif): concave_1 +0.718 D, convex_2 −1.468 D. Zernike: removing piston, tilt, defocus and astigmatism takes concave_1 from 1879.5 nm rms to 253.7 nm and reports removed defocus +0.711 D. An OPD exported by the clean-installed app differs from the golden Python result in the examples package by **0.0076 nm rms** (plane removed).

**Time per frame** (inside the GUI process, 36-core Xeon, includes reading the TIFF and the Zernike report): without reference **5.5–9.7 s** (first frame of a batch 8.0–9.7 s); with reference **7.5–8.6 s** (the reference itself is built once, ≈ 2 s extra on the first frame). Headless pipeline earlier measured ≈ 5 s/frame.

**Peak memory per frame** (working set of the whole GUI process during the frame): **396–513 MB** for a single frame, **561–635 MB** for the second frame of a batch (the previous result is cached for display). Process peak over the whole smoke test including the 3-D view and software rendering: peak working set ≈ 0.8–0.9 GB, peak commit ≈ 1.0 GB. Memory when idle after processing: ≈ 80 MB working set (private ≈ 1.0 GB while the Direct3D / MKL libraries stay loaded).

**Trimmed MKL set** (the TBB, sequential and vector-math libraries were removed from the package): re-run with `MKL_ENABLE_INSTRUCTIONS=AVX2` and `=SSE4_2` to force the other kernel sets: both completed and gave the identical +0.711 D result.

## 3. Package contents checked at build time
Self-contained win-x64, 271 files, 546 MB installed; **no XIMEA, xiApi or FFTW files**; no live-mode screen; `QwlsiApp.exe` file/product version 1.0.0; THIRD-PARTY-NOTICES.txt, USER-GUIDE.md, RELEASE-NOTES.md included. SHA-256 sums in `dist/release/SHA256SUMS.txt`.

## 4. Not tested
* A machine without a GPU (software rendering was only exercised by switching on this PC), a machine with fewer cores / less RAM, Windows 10, other CPU brands.
* The installer on a locked-down (AppLocker / managed) PC; SmartScreen behaviour for the unsigned installer.
* Camera hardware (not part of this release).
