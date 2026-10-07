# QWLSI single-frame wavefront 1.0.0 — user guide

Reconstructs the wavefront (optical path difference, OPD, and the complex field) from **one raw quadri-wave lateral shearing (QWLSI) frame**. It is a C# port of the validated Python/MATLAB reconstruction and reproduces the Python results to 0.01 nm.

## 1. Requirements
| | |
|---|---|
| System | Windows 10 or 11, 64-bit. No .NET installation needed (it is bundled). |
| RAM | **Minimum 8 GB**, 16 GB recommended. One 4512 × 4512 frame needs about 0.4–0.5 GB while processing; the whole program, with the 3-D view open, peaks at roughly 0.6–1.0 GB. |
| Disk | ≈ 0.7 GB installed. Each exported matrix text file is ≈ 13–25 MB per frame. |
| CPU | Any 64-bit CPU. The solve is multi-threaded: ≈ 5–9 s per frame was measured on a 36-core workstation; expect several times longer on a 4–8 core PC (not measured). |
| Graphics | Optional. The 3-D view uses Direct3D 11; without a usable GPU it falls back to software (WARP) rendering. |

## 2. Installation
**Installer (recommended):** run `QwlsiApp-1.0.0-setup.exe`. It installs **for the current user only — no administrator rights**, to `%LOCALAPPDATA%\Programs\QwlsiApp`, adds a Start-menu entry and (optionally) a desktop shortcut, and registers an uninstaller (Settings → Apps). The installer is not code-signed, so Windows SmartScreen may show "unknown publisher": choose *More info → Run anyway*.

**Portable zip:** unzip `QwlsiApp-1.0.0-portable.zip` anywhere and run `QwlsiApp.exe`. Nothing is written outside the folder except your settings and logs in `%APPDATA%\QwlsiApp`.

**Examples (optional):** `QwlsiApp-1.0.0-examples.zip` contains `calibration.json`, the flat/reference frame, the system wavefront `ref_OPD_m.npy`, six lens frames and the Python results to compare with (see its `README.md`). Process one frame and compare the numbers to confirm the installation.

Uninstall: Settings → Apps, or the Start-menu *Uninstall* entry. Your settings and logs in `%APPDATA%\QwlsiApp` are kept; delete that folder for a full clean-up.

## 3. Loading frames
*Offline* tab → **Open file(s)…** or **Open folder…** (optionally *include subfolders*), or drag files/folders onto the list. Frames are uncompressed 8- or 16-bit square TIFFs (4512 × 4512 for this instrument; the size must be divisible by the decimation, 3). **Process all** runs every listed frame one after the other with a progress bar and **Cancel**; **Process selected** runs the highlighted one. Click a row to show its result (the last three results are kept in memory). A frame that fails is reported in its row and the batch continues.

The result is shown as OPD, intensity, mask, |U| or the wrapped field phase, with colour map, unit and range controls (mouse wheel zooms, drag pans, the read-out shows x, y and the value). The right panel gives the **lens power** (+ = concave/bowl), astigmatism, coma, spherical aberration, RMS, PV and pupil radius, plus carriers, solver residual, time and peak memory.

## 4. Calibration (carriers)
A carrier error is mathematically identical to defocus/astigmatism, so the two carrier vectors must come from a **one-time calibration on a flat/collimated frame**. They are stored in `calibration.json` (the same file the Python and MATLAB tools use). A calibration stays valid for any sample as long as grating, camera, relay and binning do not change.

*Calibration* tab:
* **Browse…** loads a `calibration.json`.
* **Auto-find** searches, in this order: the folder of the loaded image → the program folder → the last-used path → `%APPDATA%\QwlsiApp`. The search order and the result are shown.
* **Make one:** *Add flat frame(s)…* (several frames are averaged), **Calibrate**, review the carriers, the plane-fit residual and the difference to the loaded calibration, then **Use** and/or **Save as calibration.json…**.
* **Process a flat frame…** checks the loaded calibration: a flat frame should read **0.00 D**.

The calibration in use is always shown in the status bar.

## 5. Without or with a reference
* **Without reference** (default): carriers from `calibration.json`. The OPD then **includes the static system/beam wavefront** (about 0.3–0.4 µm rms) — a yellow banner says so. To remove it, choose a *system wavefront* `.npy` (an OPD made from a flat frame; `ref_OPD_m.npy` in the examples). It is subtracted and piston/tilt are removed again; this reproduces reference-based results to ≈ 5 nm. Any flat frame processed without reference can be turned into one with **Save OPD as system wavefront…**.
* **With reference:** choose a flat reference frame. The carriers are detected from it, the sample lobes are divided by the reference lobes and the result is the sample-only OPD.

For a wavelength other than 650 nm change **λ** in *Settings*. The shear scales with λ while the measured phase scales with 1/λ, so the OPD barely depends on this setting (it matters for the complex-field phase and the exports); also re-check a flat frame (0.00 D) and make a new system wavefront.

## 6. Zernike removal
*Zernike* tab. The OPD is fitted with the **OSA/ANSI** Zernike polynomials to order 10 (66 terms; index j = (n(n+2)+m)/2, Noll index shown, unit rms; θ counter-clockwise from +x, x to the right, y up — *flip x / flip y* change this). The fit is a joint least-squares fit over the pupil circle found from the mask.

* Tick the terms to **remove** in the tree (tri-state per radial order) or pick a preset: *piston + tilt* (default), *+ defocus*, *+ astigmatism*, *up to order N*, *none*; ticking by hand switches to *custom*.
* The map shown is a **filtered copy** (Show: Filtered / Raw / Removed surface); the raw result is never modified.
* The table lists every coefficient in nm, and in dioptres for the second-order terms (defocus 4√3·c/R², + = concave; astigmatism power-vector components 2√6·c/R²). RMS and PV before/after are shown.
* A **warning** appears when defocus or astigmatism is removed, because the displayed map then no longer contains the lens power / cylinder.
* **Export** (bottom right): *Export filtered OPD*, *Export raw OPD*, *Coefficients CSV*. The removed terms are recorded in the text header and in the sidecar JSON.

## 7. 3-D view
*3D* tab: the OPD (raw or Zernike-filtered) as a height-field. Mesh **512²** (default, block-averaged), 256² or Full; vertices outside the pupil are holes. *Height exaggeration* slider (×1 is true scale — a micron of OPD on a millimetre of pupil is flat — **Auto** picks a readable value). Colour scale: **Auto** (min–max), **Manual** or **Percentile** clipping. Wireframe (use the 256² mesh for readable lines), axes in mm and nm, preset views (Isometric, Top, Front, Side, Back), and **Save PNG screenshot…**. Left-drag rotates, right-drag pans, the wheel zooms. If the GPU/driver cannot render it, tick **software rendering (WARP)** (it switches automatically on a render failure).

## 8. Exports
*Offline* tab → *Export* (or the Zernike tab). Choose the quantities (OPD, intensity, mask, complex field as Re+Im or Amp+Phase) and the formats:
* **Matrix .txt/.csv** — 1504 × 1504 numbers, one image row per line; defaults: **nm, tab, 3 decimals, NaN outside the pupil, no header** (units nm/µm/m/waves, delimiter tab/space/comma, NaN or 0). Loads with `readmatrix` (MATLAB) or `numpy.loadtxt`.
* **XYZ list** (x_mm, y_mm, OPD; origin = pupil centroid), **MATLAB .mat** (v5), **NumPy .npy** (OPD in metres, complex64 field), **float32 TIFF**, **PNG preview** with colour bar.
* Every export also writes a sidecar `*_export.json` with all parameters used (λ, pitch, carriers, mode, options, statistics, removed Zernike terms).
* *auto-export every frame during batch* writes each frame as it finishes plus `summary.csv` (lens power, astigmatism, RMS, PV, time, peak memory per frame). The default folder is `qwlsi_out` next to the frames.

## 9. Diagnostics and reporting a problem
**About / diagnostics** (bottom right) shows the version, .NET version, MKL status, GPU or software-rendering mode, calibration file in use, parameters, last result and peak memory. **Copy diagnostics** puts all of it (plus the settings in use and the last log lines) on the clipboard — paste it into your bug report. Logs are in `%APPDATA%\QwlsiApp\logs` (one file per day, kept 30 days). After an unexpected error the program writes `crash-….txt` there (exception, diagnostics and settings) and offers to copy it.

## 10. Known limitations
* **No live mode.** This release processes saved TIFF frames only; there is no camera screen and no XIMEA code. (The XIMEA interface was developed against a simulator and **has never been tested on camera hardware**.)
* **Without a reference or system wavefront the OPD contains the static system wavefront** (≈ 0.3–0.4 µm rms) — the result is the absolute wavefront, not the sample alone.
* Tilt is not absolute in a single frame; piston and tilt are removed.
* Needs the instrument's calibration; nominal carriers are not accepted (they are off by 3.7 µm rms).
* Frames must be square, uncompressed 8/16-bit TIFFs, side divisible by 3; the exports assume the 8.22 µm pitch of the default setup (set the pixel size and zoom in *Settings* for other optics).
* The installer is unsigned (SmartScreen prompt). Only Windows x64.
* Typical time is several seconds per frame; the 3-D view with the *Full* mesh (2.3 M vertices) is slow, especially in software mode.
