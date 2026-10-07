"""Reference OSA/ANSI Zernike coefficients (order 10) from the PYTHON code's own zfit, for the 7 golden frames.
Python convention: theta = arctan2(Y - cy, X - cx) with image rows going DOWN (y down). Coefficients in metres.
Usage: python tools/gen_python_zernike.py   (read-only on ..\recon_single_frame; writes src/Qwlsi.Tests/data/python_zernike_order10.json)"""
import sys, json, os
import numpy as np
sys.dont_write_bytecode = True
root = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
sys.path.insert(0, os.path.join(root, 'recon_single_frame'))
import qwlsi_single_frame as q
out = {}
for name in ['concave_1_img', 'concave_2_img', 'concave_1-5_img', 'convex_1_img', 'convex_2_img', 'convex_1-5_img', 'ref']:
    W = np.load(os.path.join(root, 'recon_single_frame', 'results', name + '_OPD_m.npy')).astype(np.float64)
    mask = ~np.isnan(W)
    z, Rm, B, m, c = q.zfit(np.nan_to_num(W), mask, 3 * q.PAR['p'] / q.PAR['Z'], nmax=10)
    yy, xx = np.nonzero(mask)
    out[name] = dict(cx=float(xx.mean()), cy=float(yy.mean()), radius_px=float(np.sqrt(mask.sum() / np.pi)), n_fit_pixels=int(m.sum()),
                     names=[list(k) for k in z.keys()], coeff_m=[float(v) for v in c])
    print(name, len(c), 'defocus c20 = %.6g nm' % (z[(2, 0)] * 1e9))
json.dump(out, open(os.path.join(os.path.dirname(__file__), '..', 'src', 'Qwlsi.Tests', 'data', 'python_zernike_order10.json'), 'w'), indent=1)
