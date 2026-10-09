# QWLSI single-frame wavefront 1.0.1 — examples

Use these files to check that the installation works and reproduces the validated results.

| File / folder | What it is |
|---|---|
| `calibration.json` | carrier vectors of this instrument (made from `ref.tif`); same file the Python/MATLAB tools use |
| `ref.tif` | flat / collimated reference frame (4512 × 4512, 650 nm) — use it for *With reference* and for *Calibrate from a flat frame* |
| `ref_OPD_m.npy` | static system wavefront (OPD in metres, NaN outside the pupil) — choose it as *system wavefront* in *Without reference* mode |
| `frames/` | six raw lens frames: concave_1, concave_2, concave_1-5, convex_1, convex_2, convex_1-5 |
| `golden/` | the Python reference OPD (`*_OPD_m.npy`, metres) of each frame |

## 1. Quick check (about a minute)
1. Start the program, *Offline* tab → **Open file(s)…** → `frames\concave_1_img.tif`.
2. *Calibration* tab → **Browse…** → `calibration.json` (or **Auto-find** after copying it next to the frames).
3. *Offline* → **Process all**. The result should read (without reference):

| Frame | Lens power | Astigmatism | RMS | PV |
|---|---|---|---|---|
| concave_1 | **+0.711 D** | 34.5 nm | 1863.6 nm | 7.432 µm |
| concave_2 | +1.410 D | 101.3 nm | 3666.8 nm | 13.509 µm |
| concave_1-5 | +1.049 D | 26.5 nm | 2724.8 nm | 10.139 µm |
| convex_1 | −0.727 D | 152.8 nm | 1898.8 nm | 7.154 µm |
| convex_2 | **−1.475 D** | 229.9 nm | 3789.0 nm | 13.961 µm |
| convex_1-5 | −1.133 D | 180.7 nm | 2941.6 nm | 11.059 µm |
| ref (flat) | **−0.007 D** (≈ 0) | 73.7 nm | 379.3 nm | 2.354 µm |

Powers agree with the Python results to ±0.001 D; the first frame takes a few seconds to tens of seconds depending on the CPU.

## 2. With a reference
Choose **With reference** and select `ref.tif`: the powers become the sample-only values +0.718 (concave_1), +1.417 (concave_2), +1.056 (concave_1-5), −0.720 (convex_1), −1.468 (convex_2), −1.126 D (convex_1-5). Alternatively stay *Without reference* and select `ref_OPD_m.npy` as the system wavefront: the same values to about 5 nm.

## 3. Calibration check
*Calibration* tab → *Add flat frame(s)…* → `ref.tif` → **Calibrate**. The new carriers must equal those in `calibration.json` to better than 1e-5 cycles/pixel (they agree to ~1e-11).

## 4. Compare an exported OPD with the golden result (optional, needs Python + numpy)
Export `.npy` (OPD in metres) from the program, then:

```python
import numpy as np
a = np.load("concave_1_img_OPD_m.npy").astype(float)        # your export
b = np.load("golden/concave_1_img_OPD_m.npy").astype(float)  # Python reference
m = np.isfinite(a) & np.isfinite(b)
y, x = np.nonzero(m)
A = np.c_[x, y, np.ones(m.sum())]
d = (a - b)[m]
r = d - A @ np.linalg.lstsq(A, d, rcond=None)[0]              # remove piston + tilt
print("rms difference: %.4f nm" % (np.sqrt((r**2).mean()) * 1e9))   # expected: about 0.01 nm
```
