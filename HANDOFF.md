# HANDOFF — QWLSI single-frame wavefront app (C# / .NET 8 / WPF, Windows x64)

Prepared 2026-10-07 for Claude Code CLI running on the user's Windows PC (user: rakeshUH, UH Physiological Optics Lab).
Working folder: `H:\caliberation\Phase_imager_cal\New\QwlsiApp\`  (parent folder holds data and the reference code).

## 1. Goal
A stand-alone, installable 64-bit Windows app built on the validated single-frame QWLSI reconstruction:
1. **Live real-time imaging** from a XIMEA camera, with **high accuracy** (same solver as offline, not an approximation).
2. **Offline processing** of saved TIFF frames (single file or batch).
3. **Calibration file handling**: browse for `calibration.json`, auto-search for it, create it from a flat frame, save it.
4. **With reference / without reference** operation.
5. **Offline OPD export as .txt matrix** (and other formats, §6).
6. A proper installer; runs on any 64-bit Windows PC without installing .NET.

## 2. Inputs already on this PC (read-only unless stated)
Parent folder `..\` = `H:\caliberation\Phase_imager_cal\New\`
- Raw frames: `concave_1_img.tif`, `concave_2_img.tif`, `concave_1-5_img.tif`, `convex_1_img.tif`, `convex_2_img.tif`, `convex_1-5_img.tif`, flat/reference `ref.tif` (4512x4512, 650 nm illumination).
- Vendor/reference-based outputs: `..\output\`.
- Python reference: `..\recon_single_frame\qwlsi_single_frame.py`; MATLAB port `.m`; `calibration.json`; `README.md`.
- GOLDEN results to test against: `..\recon_single_frame\results\*_OPD_m.npy` (float, metres), `*_field.npy` (complex), `ref_OPD_m.npy` (static system wavefront).
- XIMEA SDK: `C:\XIMEA\Examples` — .NET wrapper for 64-bit: `Bin\net7.0\xiApi.NETX64.dll` (the plain `xiApi.NET.dll` is the 32-bit wrapper and fails to load in an x64 process; also net5.0/net6.0/netcoreapp*; the namespace is still `xiApi.NET`), native `Bin\xiapi64.dll`, C# sample `Sources\xiAPI.NET.7.cs\Capture-images\Program.cs`, C header `Include\xiApi.h`, xml docs `Bin\net7.0\xiApi.NET.xml`.
- Python is available on this PC for regenerating golden data: `python ..\recon_single_frame\qwlsi_single_frame.py frame.tif --calib ..\recon_single_frame\calibration.json --out <dir>`.

## 3. System parameters (defaults; must be editable in the UI and saved in settings.json)
Gamma (cross-grating period) = 16.44e-6 m; d (grating–camera distance) = 0.11e-3 m; p (pixel) = 2.74e-6 m; Z (relay zoom) = 1;
wavelength = 650e-9 m; sensorSize = 4512; R = 695 (filter radius in FFT bins); theta = 0.5771 rad (nominal, approximate);
decimation = 3 → output grid 1504x1504, pitch = 3*p = 8.22 µm. Nominal carrier |f| = 2/6 cyc/px (1504 FFT bins), carriers orthogonal.
`calibration.json` (current): `fa=[0.2796747289760845,-0.18136218887192246]`, `fb=[-0.1813662601844535,-0.27973829485883484]` cyc/px.
Keep the exact JSON schema of the Python version so the same file works in Python, MATLAB and this app.

## 4. Algorithm (port 1:1 from `qwlsi_single_frame.py`; function names in brackets)
1. Load frame, float32. FFT (real 4512x4512).  `[load]`
2. Carriers fa, fb (cyc/px) come from calibration (or from the reference frame in with-ref mode; nominal+DFT-peak refinement only as a fallback `[nominal_carriers, dft_peak]`).
3. Demodulate three lobes — DC (zero order), A (carrier a), B (carrier b): smooth-tapered circular spectral window of radius R around the NEAREST INTEGER BIN of the carrier, inverse FFT on the decimated 1504² grid, then apply the exact sub-bin phase ramp for the fractional carrier offset. `[window, baseband]`. Amplitude-preserving scale `(M*M)/(N*N)` — do NOT add an extra DEC² factor (that was a real bug, already fixed).
4. Intensity: black level from the DC lobe (`lo = percentile(I0,10); black = mean(I0[I0 < 1.5*lo+1])`), `I = clip(I0-black,0)`, mask from lobe amplitude (`make_mask`, frac=0.15, erode=4), normalise I by the 95th percentile inside the mask. `[reconstruct]`
5. Shear model: arg A_j = sigma*(2π/λ)*[W(x+s_j/2) − W(x−s_j/2)], shear vector s_j = λ d f_j / p² (px; ≈3.17 px), sigma = −1 (sign so that a concave lens gives a bowl, matching the vendor output). `[shear_px]`
6. Solve for W: masked least squares with the exact shear operator, Fourier-preconditioned conjugate gradient, `iters=60`, `unwrap_passes=2` (re-wrap residual to nearest 2π multiple using the current estimate). `[solve_opd]` PLAIN CG WITHOUT THE PRECONDITIONER DOES NOT CONVERGE ON DEFOCUS-LIKE MODES — keep the preconditioner and the initial guess.
7. Remove piston/tilt. Output OPD (m), mask, intensity, complex field U = sqrt(I)·exp(i·2π·W/λ)·mask. Report lens power (D), astigmatism, rms, PV from the Zernike fit. `[zfit, report]`

Known physics (must be shown in the UI help):
- Without a reference the OPD includes the static system/beam wavefront (~0.3–0.4 µm rms). Subtracting `ref_OPD_m.npy` (or any flat-frame OPD) reproduces reference-based results to ~5 nm.
- A carrier error is indistinguishable from defocus/astigmatism → carriers need the one-time flat-frame calibration. `calibration.json` is valid for any sample as long as grating, camera, relay and binning are unchanged; for another wavelength set λ in the app (the shear scales with λ), re-check on a flat frame (should read 0.00 D), and make a new flat-based system wavefront.
- Do NOT port the experimental forward-model/JAX fit in `..\recon_single_frame\experimental\`; real-data validation showed it is not an improvement.

## 5. Modes and behaviour
**Calibration**: Browse button; auto-search order = folder of the loaded image → app folder → last-used path (settings.json) → `%APPDATA%\QwlsiApp`; "Calibrate from flat frame" (file or live, optional average of N frames) → show carriers and residual → "Save as calibration.json". Show the loaded calibration's path and fa/fb in the status bar.
**With reference**: "Capture reference" (live, average N frames) or load a reference TIFF / saved reference. Divide the sample lobes by the reference lobes (A/A_ref, B/B_ref), then the same shear-model solve → sample-only OPD; carriers are detected from the reference. Intensity from DC lobe as above.
**Without reference**: use calibration.json; optional "system wavefront file" (`ref_OPD_m.npy`-like, or one made from a flat frame) to subtract; otherwise show a visible note that static system wavefront is included.
**Offline**: open file or folder, process one/batch with progress + cancel, export, summary table (power, astigmatism, rms, PV) as CSV.
**Live (transmittance only)**: the live view computes and displays ONLY the transmittance map, from the DC (zero-order) lobe. Pipeline per frame: one full-frame r2c FFT → DC window → small 1504² inverse FFT → black-level removal → normalisation. It needs no carriers and no calibration.
- With a reference: show T = I / I_ref (I_ref = DC-lobe intensity of the reference frame, same pipeline). Without one: show the normalised intensity and label it "relative".
- grab loop keeps ONLY the newest frame (drop old ones), process on a worker thread, render at ≤30 fps. Measure the live rate in frames/s (Phase 0 result below) and show it in the status bar.
- **"Capture and reconstruct" button**: grabs the current frame (optionally the average of N frames) and runs the full-accuracy solve (60 CG iterations × 3 passes, same as offline, golden-validated) on a background thread, with a progress bar and cancel. The result goes to the OPD viewer and to the exports (§6). Live view keeps running meanwhile.
- **Live OPD** is an optional toggle, OFF by default. Warm-start, adaptive stopping and progressive display are NOT required; implement them only if this toggle needs them (Phase 0 data: 10 CG iterations/pass ≈ 1.2 s/frame, ≈2.3 nm from the full solve; warm start only helps when every pass starts from the current W).
- optional low-resolution preview switch for alignment (OFF by default).

## 6. Offline export (OPD and other maps)
- **Matrix .txt / .csv**: 1504 rows × 1504 columns, one image row per line (loadable with MATLAB `readmatrix`/`load`, numpy `loadtxt`). Options: delimiter (tab/space/comma), decimals, units (nm, µm, m, waves), outside-pupil as NaN or 0, optional header as `#` comment lines (λ, pitch, calibration used, mode, date) — header OFF by default for a pure matrix. Stream-write; ~20–25 MB per frame.
- **XYZ list .txt**: columns x_mm, y_mm, OPD; option to drop masked pixels.
- Also: `.mat` (v5, loads in MATLAB), `.npy` (float32 / complex64), 32-bit float TIFF, PNG preview with colormap + colorbar.
- Same options for intensity, mask, and complex field (real+imag or amplitude+phase as two matrices). Always write a sidecar JSON with all parameters used.

## 7. XIMEA camera (live source)
Use the `xiApi.NET` namespace from `Bin\net7.0\xiApi.NETX64.dll` (Phase 0: verified it loads under net8.0; the native `xiapi64.dll` is found in `C:\XIMEA\API\xiAPI` or next to the exe — pre-load it with NativeLibrary.TryLoad). API pattern from the SDK sample: `new xiCam()`, `GetNumberDevices`, `OpenDevice(i)`, `GetParam(PRM.DEVICE_NAME / DEVICE_SN, out string)`, `SetParam(PRM.EXPOSURE, µs)`, `SetParam(PRM.GAIN, dB)`, `SetParam(PRM.IMAGE_DATA_FORMAT, IMG_FORMAT.…)`, `SetParam(PRM.BUFFER_POLICY, BUFF_POLICY.SAFE)`, `GetParamInt(PRM.WIDTH/HEIGHT)`, start acquisition, `GetImage(...)`.
- Capture 16-bit (`RAW16` or `MONO16`) at full sensor bit depth, NOT the 8-bit used in the sample. Warn if the image format is 8-bit or the frame size ≠ calibration size.
- UI: camera picker (model + serial), exposure µs, gain, saturation warning + histogram, start/stop, snapshot, reference capture with averaging.
- Implement behind `ICameraSource` with adapters: `XimeaCameraSource`, `FolderWatcherSource`, `TiffStackSource`, `SimulatorSource` (replays the lens frames) so everything is testable without a camera.
- Ship `xiapi64.dll` next to the exe; the XIMEA API software package (driver) must be installed on the target PC — installer should check and tell the user if missing. If xiApi is unavailable the app must still start in offline/simulator mode.
- Probe (`src/Qwlsi.XimeaProbe`): the Phase 0 version only checks that the wrapper loads and `GetNumberDevices` returns (prints "No devices found" when none; verified — no camera on the dev PC). Live mode is developed and tested with `SimulatorSource` (lens frames + ref.tif replayed at 10 fps). See §13 for the pending hardware items.

## 8. Architecture
Solution `QwlsiApp.sln`:
- `Qwlsi.Core` (net8.0, no UI): carriers/calibration IO, window+demodulation, intensity/mask, shear model, CG solver, Zernike/report, complex field, exporters.
- `Qwlsi.Camera` (net8.0-windows): `ICameraSource`, XIMEA + file/simulator sources.
- `Qwlsi.App` (net8.0-windows, WPF, MVVM): views for Live / Offline / Calibration / Settings; WriteableBitmap viewer with colormaps, FFT view marking lobes, profile line, results panel.
- `Qwlsi.Tests` (xUnit): golden tests (§10).
- FFT: native FFTW (`libfftw3f-3.dll`, bundled) or Intel MKL via a thin P/Invoke wrapper; real-to-complex where possible; plan reuse; parallel across cores. Benchmark both in Phase 0 and choose; pure-managed FFT is too slow for 4512². Float32 throughout; preallocate; no per-frame allocations on the hot path.
- TIFF IO: a maintained library (e.g. BitMiracle.LibTiff.NET); handle 8/12/16-bit, big frames.
- Packaging: `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`; installer with Inno Setup (per-user, no admin) + portable zip; provide `build.bat` that publishes and builds the installer in one step. Settings in `%APPDATA%\QwlsiApp\settings.json`.

## 9. Phases (finish each and report before the next)
0. **Spike**: FFT benchmark (4512² r2c and 1504² c2c, FFTW vs MKL) on this PC; port `Qwlsi.Core`; run on all six lens frames and ref with `calibration.json`; compare with golden results (§10); measure seconds/frame cold and warm-started; XIMEA probe grabs one 16-bit frame. REPORT real timings.
1. **Offline app**: UI, load/batch, with/without ref, calibration browse/auto-find/make/save, all exports.
2. **Live mode**: XIMEA source, live transmittance view, "Capture and reconstruct" (full 60×3 solve, background, cancel), optional live-OPD toggle, reference capture, live calibration.
3. **Packaging**: self-contained publish, installer, driver check, test on a clean machine/VM, short user guide.
4. Optional: GPU FFT if Phase 0/2 timings show the CPU rate is too low.

## 10. Acceptance tests (write them as xUnit tests against the golden data)
- OPD vs `results\<frame>_OPD_m.npy`: rms difference ≤ 5 nm inside the mask on all six lens frames and ref (the Octave port achieved ≤ 6 nm; piston/tilt removed on both sides before comparing).
- Intensity: relative difference ≤ 0.01 % vs Python; |U|² correlates ≥ 0.98 with the smoothed raw frame.
- Lens powers (Zernike defocus, + = concave): concave_1 ≈ +0.72 D, convex_2 ≈ −1.47 D (±0.01 D); ref (flat) ≈ 0.00 D with its own carriers. Regenerate exact values by running the Python script.
- With-ref mode (reference = ref.tif): the sample OPD equals (single-frame OPD − ref_OPD_m) to ≤ 10 nm rms, tilt removed.
- Calibration round-trip: calibrate from `ref.tif` → carriers within 1e-5 cyc/px of the JSON above; JSON read/written bit-compatible with Python.
- Matrix export: 1504×1504 numbers, round-trips in numpy `loadtxt`/MATLAB `readmatrix` to the printed precision.
- Live simulator: continuous 1-minute run with no memory growth, no frame backlog, UI stays responsive.

## 11. Lessons learned (do not repeat)
- Extra DEC² (=9) scaling in the spectral window broke intensity; black-level must be removed from the DC lobe.
- Plain CG fails on low-order modes; the Fourier preconditioner + initial guess fix it.
- sigma = −1 is the sign convention that matches the vendor map.
- A 4 GB memory limit applied to the old Python run (double precision); in C# use float32 and process one frame at a time.
- Carrier calibration ≡ defocus/astig calibration; never trust nominal carriers (3.7 µm rms error).

## 12. Ask the user before assuming
Camera model/serial and bit depth; whether other wavelengths will be used; preferred default units for text export; whether an installer needs code signing; whether Mac/Linux is ever needed (current plan: Windows x64 only).

## 13. Pending items (need hardware)
- **Camera frame-rate measurement**: live transmittance fps with a real XIMEA camera, including readout and display (Phase 0 in-memory figure: 14-17 fps). Not measured: no camera is connected to the dev PC.
- **Camera probe extension**: open the device, print model/serial/sensor size/bit depth, grab one 16-bit frame, save it as TIFF; ask the user for the camera model if DEVICE_NAME cannot be read.

## 14. Status (updated after Phase 1)
- Phase 0 accepted. Phase 1 (offline app) implemented: `src/Qwlsi.App` (WPF, MVVM). Screenshots in `docs/screenshots/` (produced by `QwlsiApp.exe --selftest <dir> --file a.tif;b.tif --ref ref.tif --calib calibration.json --flat ref.tif`).
- Memory per frame (peak working set): headless pipeline ~350-400 MB without reference, ~430-520 MB with reference (was 695-722 MB); GUI ~600 MB peak (incl. WPF, 120 MB baseline). Idle after trim: ~80 MB working set.
- Not in Phase 1 (deferred): FFT view marking lobes, profile line in the viewer, FolderWatcher/TiffStack sources, live screen (Phase 2), installer (Phase 3).

## 15. Zernike removal (added after Phase 1)
- **Basis**: OSA/ANSI Z80.28, orders 0-10 (66 terms), index `j = (n(n+2)+m)/2`, each term normalised to unit rms over the unit disk, `Z = N R(rho) cos(m theta)` for m>=0 and `N R(rho) sin(|m| theta)` for m<0, `N = sqrt((2-delta_m0)(n+1))`. Names: 0 piston, 1 vertical tilt `2 rho sin t`, 2 horizontal tilt `2 rho cos t`, 3 oblique astigmatism `sqrt6 rho^2 sin 2t`, 4 defocus `sqrt3 (2 rho^2-1)`, 5 vertical astigmatism, 6 oblique trefoil, 7 vertical coma, 8 horizontal coma, 9 horizontal trefoil, 10 oblique quatrefoil, 11 secondary oblique astigmatism, 12 spherical `sqrt5 (6 rho^4-6 rho^2+1)`, 13 secondary vertical astigmatism, 14 horizontal quatrefoil `sqrt10 rho^4 cos 4t`, then the same pattern to order 10. Noll index shown alongside. Code: `Qwlsi.Core/ZernikeOsa.cs` (single implementation; `Zernike.Report` now uses it).
- **Orientation**: theta counter-clockwise from +x, x to the right, **y up (image rows flipped)**. Settings `FlipX` / `FlipY` (Zernike tab) undo that; `FlipY = true` reproduces the Python reference convention (y down). Power, astigmatism magnitude, coma magnitude and spherical are orientation independent; only the sign of sine terms (m<0) and of odd-m terms under an x-flip changes.
- **Fit**: joint masked least squares of all 66 terms over the mask-fitted circle (centre = mask centroid, radius = sqrt(area/pi)), pixels with rho<=1. Subtraction is evaluated at every valid mask pixel (also rho>1). Raw result is never modified (non-destructive filtered view; raw / filtered / removed-surface display).
- **UI (Zernike tab)**: checkbox tree by radial order (tri-state groups), presets *piston + tilt* (default), *+ defocus*, *+ astigmatism*, *up to order N*, *custom*, *none*; coefficient table (j, Noll, n, m, name, nm, diopters, removed); rms/PV before and after; warnings when defocus (j=4) or astigmatism (j=3,5) is removed. Diopters are given for the second-order terms: defocus `4 sqrt3 c / R^2` (+ = concave), astigmatism power-vector components `2 sqrt6 c / R^2`.
- **Export**: option "export OPD with the Zernike terms removed" (Offline tab, export panel; also applies to batch auto-export). File names get `_OPD_filtered_`; the text header records the convention, fit order and every removed term with coefficient; the sidecar JSON has a `zernike_removal` block (circle, removed terms with ANSI/Noll/n/m/name/nm/diopters, rms/PV before and after, warnings).
- **Validation** (`ZernikeTests`): closed forms for j=0..14, orthonormality to order 10, Noll mapping, recovery of known coefficients on the real mask for all four orientation settings, sign rules of the flip flags, **+0.7110 D on concave_1** (order-6 report; order-10 joint fit gives +0.7109 D), and coefficient-by-coefficient agreement with the Python fit (`tools/gen_python_zernike.py` -> `src/Qwlsi.Tests/data/python_zernike_order10.json`) on all 7 frames: fit of Python's own OPD agrees to <=5e-11 nm; C# pipeline OPD agrees to <=0.003 nm.

## 16. 3-D viewer (added after Phase 1)
- HelixToolkit.SharpDX.Core.Wpf 2.27.3 (SharpDX / Direct3D 11). `Qwlsi.Core/SurfaceMesh.cs` prepares the mesh without any GPU dependency; `Qwlsi.App/Controls/Surface3DView.*` renders it.
- **Mesh**: default 512 x 512 (area-weighted block average of the 1504 x 1504 map, fractional box M/n); 256 x 256 and Full (1504 x 1504) as options. Holes outside the pupil (a vertex is valid when >= half of its box is valid; triangles only where all four quad corners are valid). The 512 grid spans the whole field, so the pupil itself covers ~350 x 350 vertices (24 um cells).
- **Controls**: source (raw / Zernike-filtered), mesh size, height exaggeration (log slider 1 .. 1e5, "Auto" = OPD range drawn as ~24 % of the lateral extent; x1 = true scale), colour scale (Auto min/max, Manual, Percentile clip) with colormap and a colour bar, wireframe toggle, axes in mm (x, y) and nm (OPD), preset views (Isometric, Top, Front, Side, Back), PNG screenshot (`Viewport3DX.RenderBitmap`), mouse rotate / pan / zoom.
- **Software rendering fallback**: checkbox "software rendering (WARP)" (Direct3D 11 WARP CPU rasteriser via `EffectsManagerConfiguration.EnableSoftwareRendering`); switched on automatically if the hardware render path raises an exception. Verified on this PC: both hardware and WARP render the same scene.
- Notes: wireframe on the 512 mesh is very dense (looks like a solid fill) - use the 256 mesh for readable lines. The 3-D tab adds ~150-250 MB private memory. 3-D screenshots in the self-test use a screen grab (WPF cannot render a D3D swap chain into an offscreen bitmap).
- Tests: `SurfaceMeshTests` (resolution / cell size, block average, holes, triangulation, positions y-up, colour ranges, ticks, auto exaggeration). The rendering itself is verified by the self-test screenshots (`docs/screenshots/12*..15*`), not by unit tests.
- **Crash fix (3-D viewport mouse-down) - root cause found**: every click in the 3-D viewport threw `NullReferenceException` in `ViewBoxNode/ScreenSpacedNode.OnHitTest` (Helix hit-tests its view box on each mouse-down) and showed the modal WinForms crash dialog. It only happens AFTER the viewport's `EffectsManager` has been replaced on the live control (the "software rendering (WARP)" checkbox, or the automatic fallback): the view-box node is left stale. Reproduced with scripted mouse input (swallowed-exception counter rose on every click after the swap, never before). Fix: (1) `Surface3DView` now RE-CREATES the `Viewport3DX` (and disposes the old viewport and its effects manager) whenever the renderer changes, instead of swapping the manager in place; (2) `SafeViewport3DX` swallows any remaining `NullReferenceException` in `OnPreviewMouseDown` (first 3 are logged to `%TEMP%/qwlsi_error.log`); (3) WinForms thread exceptions from the hosted D3D surface are logged with a status-bar note instead of a modal dialog; (4) mouse-button hit testing and the view cube are off (the app never picks objects). `EnableMouseButtonHitTest=False` alone did NOT prevent the exception (it is not the gate for the view-box test). The self-test now clicks / drags / wheels in the viewport after each state change (empty, populated, WARP, back to hardware, tab leave/return, mesh size, Zernike-filtered) and requires zero exceptions.
- **Exit hang (found while fixing the above)**: Helix leaves a foreground `AsyncActionThread` per disposed viewport, which keeps the process alive after the window is closed. `App.OnExit` therefore starts a background watchdog that calls `Environment.Exit` 2.5 s after a normal WPF shutdown.
