# QWLSI single-frame wavefront — release notes

## 1.0.0 (2026-10-07)

First release. Offline reconstruction of the wavefront (OPD and complex field) from a single raw QWLSI frame — a C# / .NET 8 / WPF port of the validated Python/MATLAB code, **numerically identical to the Python reference** (OPD difference ≤ 0.01 nm rms, intensity 0.0000 %, complex field ≤ 0.01 %, lens power within 0.001 D).

### What is in this release
* **Reconstruction**: sub-bin-exact demodulation, masked least-squares shear inversion (Fourier-preconditioned conjugate gradient, 60 iterations × 3 passes), intensity and complex field, lens power / astigmatism / coma / spherical / RMS / PV report. Intel MKL FFTs; float32; ≈ 0.4–0.5 GB peak memory per frame.
* **Offline app**: open files or folders (drag and drop), batch with progress and cancel, with or without reference, optional system-wavefront subtraction, results viewer (OPD, intensity, mask, |U|, phase).
* **Calibration**: browse, auto-find, make from one or several flat frames (with convergence diagnostics), save as `calibration.json` (same schema as the Python/MATLAB tools), check on a flat frame.
* **Zernike removal**: OSA/ANSI to order 10 (66 terms, Noll index shown), joint masked least-squares fit, non-destructive filtered view, checkbox tree with presets, coefficient table (nm and dioptres), before/after RMS and PV, warnings when defocus or astigmatism is removed, export of raw or filtered maps with the removed terms recorded.
* **3-D viewer** (HelixToolkit / SharpDX): 512² / 256² / full mesh, holes outside the pupil, height exaggeration, Auto / Manual / Percentile colour scale, wireframe, axes in mm and nm, preset views, PNG screenshots, software-rendering (WARP) fallback.
* **Exports**: matrix `.txt/.csv` (1504 × 1504; nm, tab, no header by default; nm/µm/m/waves, delimiters, NaN or 0, optional header), XYZ list, MATLAB `.mat`, NumPy `.npy`, float32 TIFF, PNG preview, sidecar JSON with every parameter used, batch summary CSV.
* **Diagnostics**: daily log in `%APPDATA%\QwlsiApp\logs`, crash reports (exception + diagnostics + settings), *About / diagnostics* window with a **Copy diagnostics** button.
* **Packaging**: per-user installer (no administrator rights, Start-menu entry, optional desktop shortcut, uninstaller), portable zip, optional examples package, `THIRD-PARTY-NOTICES.txt`, user guide.

### Known issues and limitations
1. **No live mode.** The live-view screen and the camera code are not part of this release. The XIMEA interface was written and tested only against a simulator and **has never been tested on camera hardware**; no XIMEA files are shipped.
2. **Without a reference or a system-wavefront file the OPD contains the static system/beam wavefront** (≈ 0.3–0.4 µm rms). This is physical, not a bug; the program shows a banner.
3. **The installer and the program are not code-signed.** Windows SmartScreen may warn ("unknown publisher").
4. **Processing time depends on the CPU core count.** ≈ 5–9 s per frame on the 36-core development workstation; not measured on small PCs (expect several times longer on 4–8 cores).
5. **3-D viewer**: the *Full* mesh (2.3 M vertices) is slow, especially in software mode; wireframe on the 512² mesh is so dense it looks like a fill (use 256²); axis labels can overlap the surface in some views. Software (WARP) rendering was verified on the development PC but not on a PC without any GPU.
6. **The 3-D viewer has no automated unit tests** (it needs a window); it is covered by a scripted UI smoke test (`QwlsiApp.exe --selftest <folder> --file <frame.tif> …`, which also clicks, drags and zooms in the 3-D view and checks for exceptions). The reconstruction, Zernike, export, calibration, batch and live-simulator logic have 94 automated tests, all passing.
7. Helix leaves a background thread per 3-D viewport; the program therefore forces the process to end 2.5 s after the window closes.
8. Frames must be square, uncompressed 8/16-bit TIFFs with a side divisible by 3. Other optics need the pixel size, zoom and grating values in *Settings* and their own calibration.
9. Piston and tilt are removed (tilt is not absolute in a single frame). The default Zernike convention is y up with θ counter-clockwise; *flip x / flip y* are provided.
10. The wavelength setting barely changes the OPD (the shear scales with λ); it matters for the complex-field phase.
11. Uninstalling keeps `%APPDATA%\QwlsiApp` (settings, logs).
12. Early development builds of the self-test overwrote the developer's own `settings.json`; fixed — the self-test now uses a private folder.

### Fixed during the 1.0 cycle (for reference)
* 3-D viewport: `NullReferenceException` in Helix's view-box hit test on every click after switching the renderer (software ↔ hardware) — the viewport is now re-created on a renderer change.
* Memory: peak per frame reduced from ≈ 700 MB to ≈ 380–520 MB.

### Verification
94 xUnit tests (golden OPD / intensity / complex field vs Python on 7 frames, Zernike coefficients vs Python, calibration, with-reference, exports read back with numpy / scipy / tifffile, batch cancel, simulator, 60 s soak). Clean-location install tests: see `docs/TEST-REPORT-1.0.0.md`.
