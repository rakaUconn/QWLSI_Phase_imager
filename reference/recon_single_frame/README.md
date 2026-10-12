# Single-frame QWLSI reconstruction (no per-measurement reference)

`qwlsi_single_frame.py` — Python (numpy/scipy/tifffile/matplotlib).  
    python qwlsi_single_frame.py frame.tif --calib calibration.json --out results
    python qwlsi_single_frame.py --calibrate-from ref.tif --calib calibration.json     # one-time

Outputs per frame (`results/`): `*_OPD_m.npy` (metres, NaN outside pupil, 1504x1504, 3 px = 8.22 µm pitch), `*_field.npy`
(complex64, U = sqrt(I) exp(i 2π OPD/λ)), `*_recon.mat` (same, for MATLAB), `*_summary.png`.

## Method
1. FFT, sub-bin-exact demodulation of both carrier peaks (radius R = 695 bins; your 1390² vendor grid = this window).
2. arg A_j = −(2π/λ)[W(x+s_j/2) − W(x−s_j/2)],  s_j = λ d f_j / p² (|s| = 3.17 px = 2λd/Γ ⇔ your α = Γ/(4πd)).
3. Masked least-squares inversion with the exact shear operator (preconditioned CG; re-wrapping passes). Validated on a
   synthetic frame: 0.1 nm error. (Poisson/zero-fill integration, as in the reference code, under-estimates amplitude by ~9 % at the pupil edge.)
4. Intensity from the zero-order lobe; complex field assembled from both.

## Limits (important)
* Carrier-vector error ≡ defocus/astigmatism. Nominal carriers (|f| = 2p/Γ) are off by 0.24 bin on the second axis in this
  system → 3.7 µm rms error. Carriers are therefore a **one-time system calibration** (`calibration.json`, from ref.tif).
* Result is the *absolute* wavefront: it contains the beam/grating static wavefront (~0.38 µm rms, `results/ref_OPD_m.npy`).
  Subtract it to reproduce reference-based results to ~5 nm.
* Tilt is not absolute in a single frame (removed). Field is at the sensor plane; grating plane is d = 0.11 mm earlier (negligible for smooth fields).

## Validation vs reference-based (same LS solver, ref phase divided out)
power [D] single-frame / ref-based: concave 0.711/0.718, 1.049/1.056, 1.410/1.417; convex −0.727/−0.720, −1.132/−1.126, −1.475/−1.468.
Residual difference ≈ 280 nm rms, identical for all frames (= the static system wavefront).

## MATLAB version
`qwlsi_single_frame.m` (base MATLAB R2016b+, no toolboxes; same algorithm and parameters as the Python script)

    qwlsi_single_frame('--calibrate','..\ref.tif','calibration.json');            % one-time
    out = qwlsi_single_frame('..\concave_1_img.tif','calibration.json','results_m');
    imagesc(out.OPD*1e6); axis image; colorbar                                     % OPD [um]; out.field = complex field

Ported and checked in Octave 8 against the Python results: concave_1 OPD agrees to 6 nm rms (power 0.712 vs 0.711 D),
calibration carriers agree to 1e-8 cyc/px. Not run in MathWorks MATLAB itself. Note: the file holds one frame at a time (~4 GB RAM is plenty).

## Correction (2026-10-06): intensity / |U| scaling
An earlier version scaled the zero-order lobe by an extra factor DEC² = 9 and did not remove the camera black level, so the
amplitude map |U| (and the intensity) was distorted: I_old = 9·I − 8·mean. **OPD and lens powers were not affected** (they only use phases).
Fixed in both `qwlsi_single_frame.py` and `.m`; all results/ files were regenerated. Check: |U|² vs the smoothed raw frame correlates 0.984,
MATLAB(Octave) vs Python intensity agree to 0.002 %.

## Experimental: forward-model (SAFARI-style) refinement — `experimental/`
Fits the complex field directly to all 5 independent spectral lobes (incl. the two diagonal ones the demodulation pipeline ignores)
with the exact 4-beam shear model. Needs `pip install jax jaxlib`.  `python run_synthetic_test.py 1500 300` reproduces the table below.
Synthetic frame, known truth, noise + weak extra grating orders (rms OPD error, nm):
| distance from pupil edge (grid px) | demod pipeline | forward fit |
|---|---|---|
| 1–3 | no data | 24 |
| 3–6 | 3.8 (56 % covered) | 3.9 |
| 6–60 | 2–3 | 2.4–3.8 |
Real frame (concave_1): fit agrees with the pipeline to 31 nm rms, but differs by up to ±100 nm in the same fan-like pattern as the fixed
grating/beam systematic, and the diagonal lobes only fit to 8–13 % — i.e. real-data accuracy is limited by that systematic, not by the algorithm,
and the edge ring cannot be validated without truth. It costs ~1 s/iteration at full size (≈3 min for 150 it). Not used for the delivered results.

## Real-data validation of the forward fit (experimental/validation_results.json, val_*.png)
No ground truth exists on real frames, so three checks (frames concave_1 and convex_2, reference-corrected lobes, 200 L-BFGS iterations):
1. **Edge ring does not exist on real data.** The demodulation mask already covers 99.998 % of the illuminated pupil (50 % of median zero-order
   intensity): 19 of 818,720 px missing (concave_1), 17 of 814,044 (convex_2). The real pupil edge is a gradual ramp, unlike the sharp synthetic one.
2. **Hold-out lobes.** Fit with DC+fa+fb only → B−A = 11 nm rms (concave_1), 13–29 nm (convex_2): the fit reproduces the demodulation result.
   Fit with all 5 lobes → B−A = 36–82 nm (concave_1), 48–111 nm (convex_2), growing toward the edge, in the fan-like pattern of the fixed systematic.
   The held-out diagonal lobes are predicted only to 9–12 % (vs ≤0.3 % with the true field on synthetic data) — the ideal 4-wave model does not describe them.
   Per-order amplitude imbalance does not explain it (fitted gains stay 1).
3. **Lens parameters** agree: power 0.718/0.718/0.714 D (A / hold / all-lobes, concave_1), −1.468/−1.466/−1.455 D (convex_2); astigmatism within 3 nm.
**Conclusion:** adding the diagonal lobes makes the real-data result worse, not better; the forward fit cannot be validated as an improvement and is not used.
The 3-lobe fit is equivalent to the demodulation pipeline (within ~10–30 nm), so the delivered results stand.
