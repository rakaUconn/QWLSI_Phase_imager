#!/usr/bin/env python3
"""
Single-frame QWLSI (cross-grating) wavefront reconstruction -- no reference capture per measurement.

Pipeline
  1. FFT the raw frame, locate the two orthogonal carrier peaks (+/-fa, +/-fb), demodulate each with a
     sub-bin-exact carrier (no integer-bin rounding) and a circular low-pass of radius R.
  2. Two complex lateral-shear interferograms A_a, A_b.  Their phases are
         arg A_j = sigma * (2*pi/lambda) * [ W(x + s_j/2) - W(x - s_j/2) ]        (sigma = -1, vendor sign)
     with shear vector s_j = lambda*d*f_j/p^2 [px]  (|s| = 2*lambda*d/Gamma = Baffou's alpha = Gamma/(4*pi*d)).
  3. W (OPD) is obtained by a masked, shear-aware least-squares inversion (preconditioned CG, exact finite-
     difference operator, no gradient approximation, no periodic-boundary / zero-fill edge bias).
  4. Complex field  U = sqrt(I) * exp(i*2*pi*W/lambda), I from the zero-order (DC) lobe.

What a single frame can NOT give you from first principles
  Carrier-vector errors are mathematically identical to defocus/astigmatism of the wave.  The carrier
  vectors are therefore taken from a ONE-TIME system calibration (calibration.json, made once from any
  flat/collimated frame with --calibrate-from).  Without it the nominal carriers (|f| = 2p/Gamma, orthogonal)
  are used and absolute defocus/astigmatism are only good to ~ +/-1 D here; everything of higher order is fine.
Outputs are piston- and tilt-free (tilt is not absolute in a single frame).
"""
import numpy as np, os, sys, json, argparse
import scipy.fft as sfft
from scipy.sparse.linalg import LinearOperator, cg
from scipy.ndimage import binary_erosion, binary_opening, binary_fill_holes
from scipy.optimize import minimize
import tifffile
from math import factorial

# ---------------------------------------------------------------- parameters (from the user)
PAR = dict(Gamma=16.44e-6, d=0.11e-3, p=2.74e-6, Z=1.0, lam=650e-9, R=695, DEC=3)

# ---------------------------------------------------------------- helpers
def load(path): return tifffile.imread(path).astype(np.float32)

def window(F, kc, N, M, R, taper=0.08):
    """M x M spectral window around integer bin kc, circular smooth-edged mask radius R -> spatial complex image
       sampled every DEC=N/M px, carrier kc/N removed exactly."""
    m = np.arange(-M//2, M//2)
    sub = F[np.ix_((kc[1]+m) % N, (kc[0]+m) % N)]
    yy, xx = np.meshgrid(m, m, indexing='ij'); r = np.hypot(xx, yy)
    w = np.clip((R-r)/(taper*R), 0, 1); w = 0.5-0.5*np.cos(np.pi*w)
    z = sfft.ifft2(sfft.ifftshift(sub*w.astype(np.float32)), workers=2)*(M*M)/(N*N)
    return z.astype(np.complex64)

def baseband(F, f, N, M, R):
    DEC = N//M
    kc = (int(round(f[0]*N)), int(round(f[1]*N)))
    z = window(F, kc, N, M, R)
    n = np.arange(M)*DEC
    d = (f[0]-kc[0]/N, f[1]-kc[1]/N)
    return z*np.exp(-2j*np.pi*(d[0]*n[None, :]+d[1]*n[:, None])).astype(np.complex64)

def dft_peak(img, f0):
    """sub-bin carrier position = maximum of |DFT| (valid for a ~flat wave; biased by strong curvature)"""
    N = img.shape[0]; x = np.arange(N, dtype=np.float64)
    def neg(p):
        return -abs(np.exp(-2j*np.pi*p[1]*x) @ (img @ np.exp(-2j*np.pi*p[0]*x)))
    return minimize(neg, np.asarray(f0, float), method='Nelder-Mead', options=dict(xatol=1e-9, fatol=1e-3)).x

def nominal_carriers(img, F, par=PAR):
    """nominal: |f| = 2p/Gamma exactly, fb = fa rotated by -90 deg, orientation measured from the frame"""
    N = img.shape[0]; f0 = 2*par['p']/par['Gamma']
    k = sfft.fftfreq(N)
    KX, KY = np.meshgrid(k, k)
    ann = np.abs(np.hypot(KX, KY)-f0) < 20/N
    mag = np.abs(F)*(ann & (KX > 0))
    iy, ix = np.unravel_index(np.argmax(mag), mag.shape)
    fa = dft_peak(img-img.mean(), (k[ix], k[iy]))
    th_a = np.arctan2(fa[1], fa[0])
    # second peak (rotated -90 deg) for an orthogonality-averaged angle
    fb0 = f0*np.array([np.cos(th_a-np.pi/2), np.sin(th_a-np.pi/2)])
    fb = dft_peak(img-img.mean(), fb0)
    th_b = np.arctan2(fb[1], fb[0])+np.pi/2
    th = np.angle(np.exp(1j*th_a)+np.exp(1j*th_b))
    fa = f0*np.array([np.cos(th), np.sin(th)]); fb = f0*np.array([np.cos(th-np.pi/2), np.sin(th-np.pi/2)])
    return fa, fb, th

def shear_px(f, par=PAR):  # shear vector [sensor px] for carrier f [cyc/px]
    return par['lam']*par['d']*np.asarray(f)/par['p']**2

def make_mask(A1, A2, frac=0.15, erode=4):
    a = np.minimum(abs(A1), abs(A2)); m = a > frac*np.percentile(a, 95)
    m = binary_fill_holes(binary_opening(m, iterations=3))
    return binary_erosion(m, iterations=erode) if erode else m

def remove_piston(A, m): return A*np.exp(-1j*np.angle(np.sum(A[m])))

# ---------------------------------------------------------------- reconstruction
def solve_opd(A1, A2, fa, fb, mask, par=PAR, sigma=-1, iters=60, unwrap_passes=2, verbose=True):
    """masked least-squares inversion of the two shear phases -> OPD W [m] on the DEC-px grid"""
    M = A1.shape[0]; DEC = par['DEC']
    ky = sfft.fftfreq(M, d=DEC)[:, None]; kx = sfft.fftfreq(M, d=DEC)[None, :]
    H = [(2j*np.sin(np.pi*(kx*s[0]+ky*s[1]))).astype(np.complex64) for s in (shear_px(fa, par), shear_px(fb, par))]
    c = sigma*2*np.pi/par['lam']
    V = mask.astype(np.float32)
    fw = lambda x: sfft.fft2(x, workers=2)
    iw = lambda X: sfft.ifft2(X, workers=2).real.astype(np.float32)
    D = lambda W, j: iw(H[j]*fw(W)); DT = lambda r, j: -iw(H[j]*fw(r))
    S = (abs(H[0])**2+abs(H[1])**2).astype(np.float32)
    Pinv = 1/(S+S.max()*1e-6+1e-12); Pinv[0, 0] = 0
    Aop = LinearOperator((M*M,)*2, matvec=lambda w: (sum(DT(V*D(w.reshape(M, M), j), j) for j in range(2))+1e-9*w.reshape(M, M)).ravel(), dtype=np.float32)
    Pop = LinearOperator((M*M,)*2, matvec=lambda r: iw(Pinv*fw(r.reshape(M, M))).ravel(), dtype=np.float32)
    Ap = [remove_piston(A, mask) for A in (A1, A2)]
    ph = [np.angle(A) for A in Ap]; W = np.zeros((M, M), np.float32)
    for it in range(1+unwrap_passes):
        b = sum(DT(V*ph[j]/c, j) for j in range(2))
        x, info = cg(Aop, b.ravel(), x0=iw(Pinv*fw(b)).ravel(), M=Pop, maxiter=iters, rtol=1e-7)
        W = x.reshape(M, M)
        pred = [c*D(W, j) for j in range(2)]
        if it < unwrap_passes:   # re-wrap residual around the model => robust against 2*pi jumps, then drop mean (=tilt)
            ph = [pred[j]+np.angle(Ap[j]*np.exp(-1j*pred[j])) for j in range(2)]
            ph = [p-p[mask].mean() for p in ph]
    res = [float(np.sqrt(((V*(pred[j]-ph[j]))[mask]**2).mean())) for j in range(2)]
    if verbose: print('   LS residual rms [rad]: %.3f %.3f   (data rms %.3f %.3f)' % (res[0], res[1], ph[0][mask].std(), ph[1][mask].std()))
    return W.astype(np.float32), ph, res

# ---------------------------------------------------------------- Zernike report
def zern_basis(rho, th, nmax):
    out, names = [], []
    for n in range(nmax+1):
        for m in range(-n, n+1, 2):
            am = abs(m); Rr = np.zeros_like(rho)
            for s in range((n-am)//2+1):
                Rr += (-1)**s*factorial(n-s)/(factorial(s)*factorial((n+am)//2-s)*factorial((n-am)//2-s))*rho**(n-2*s)
            out.append(Rr*(np.cos(am*th) if m >= 0 else np.sin(am*th))*np.sqrt((2-(m == 0))*(n+1))); names.append((n, m))
    return np.array(out), names

def zfit(W, mask, pitch, nmax=6):
    yy, xx = np.nonzero(mask); cy, cx = yy.mean(), xx.mean(); r = np.sqrt(mask.sum()/np.pi)
    Y, X = np.indices(W.shape); rho = np.hypot(Y-cy, X-cx)/r; th = np.arctan2(Y-cy, X-cx)
    B, names = zern_basis(rho, th, nmax); m = mask & (rho <= 1)
    c, *_ = np.linalg.lstsq(B[:, m].T, W[m], rcond=None)
    z = dict(zip(names, c)); Rm = r*pitch
    return z, Rm, B, m, c

def report(W, mask, pitch):
    z, Rm, B, m, c = zfit(W, mask, pitch)
    Dpow = 2*(2*np.sqrt(3)*z[(2, 0)]/Rm**2)       # curvature of the best-fit sphere, [D]; + = bowl (concave/diverging)
    ast = np.hypot(z[(2, 2)], z[(2, -2)]); coma = np.hypot(z[(3, 1)], z[(3, -1)])
    return dict(power_D=float(Dpow), astig_rms_nm=float(ast*1e9), coma_rms_nm=float(coma*1e9), spherical_rms_nm=float(z[(4, 0)]*1e9),
                pupil_radius_mm=float(Rm*1e3), rms_nm=float(W[m].std()*1e9), pv_um=float((W[m].max()-W[m].min())*1e6))

# ---------------------------------------------------------------- calibration (one-time)
def calibrate_from_flat(path, par=PAR, R=None):
    """refine carrier vectors from a frame of a flat/collimated wave: demodulate, fit the phase plane, update f (x2)."""
    img = load(path); N = img.shape[0]; M = N//par['DEC']; R = R or par['R']
    F = sfft.fft2(img-img.mean(), workers=2)
    fa, fb, th = nominal_carriers(img, F, par)
    out = []
    for f in (fa, fb):
        f = np.array(f, float)
        for _ in range(3):
            A = baseband(F, f, N, M, R); out_mask = abs(A) > 0.5*np.percentile(abs(A), 95)
            out_mask = binary_erosion(binary_fill_holes(binary_opening(out_mask, iterations=3)), iterations=6)
            A = remove_piston(A, out_mask); ph = np.angle(A)
            Y, X = np.indices(A.shape); X = X*par['DEC']; Y = Y*par['DEC']
            G = np.c_[X[out_mask], Y[out_mask], np.ones(out_mask.sum())]
            cc, *_ = np.linalg.lstsq(G, ph[out_mask], rcond=None)
            f = f+cc[:2]/(2*np.pi)
        out.append(f)
    return dict(fa=out[0].tolist(), fb=out[1].tolist(), source=os.path.basename(path), note='carrier vectors [cyc/px] such that a flat wave gives zero differential-phase gradient')

# ---------------------------------------------------------------- full pipeline for one frame
def reconstruct(path, calib=None, par=PAR, verbose=True):
    img = load(path); N = img.shape[0]; DEC = par['DEC']; M = N//DEC; R = par['R']
    mu = img.mean(); F = sfft.fft2(img-mu, workers=2)
    if calib:
        fa, fb = np.array(calib['fa']), np.array(calib['fb']); mode = 'calibrated carriers'
    else:
        fa, fb, th = nominal_carriers(img, F, par); mode = 'nominal carriers (theta=%.4f rad)' % th
    A1 = baseband(F, fa, N, M, R); A2 = baseband(F, fb, N, M, R)
    I0 = window(F, (0, 0), N, M, R).real+mu            # zero-order lobe = intensity (sum of the 4 replicas)
    mask = make_mask(A1, A2)
    if verbose: print(' ', os.path.basename(path), '|', mode)
    W, ph, res = solve_opd(A1, A2, fa, fb, mask, par, verbose=verbose)
    pitch = DEC*par['p']/par['Z']
    W = np.where(mask, W-W[mask].mean(), np.nan).astype(np.float32)
    lo = np.percentile(I0, 10); black = I0[I0 < 1.5*lo+1].mean()      # dark level (outside the pupil the zero-order lobe is just the offset)
    I = np.clip(I0-black, 0, None); I = np.where(mask, I/np.percentile(I[mask], 95), 0)
    U = (np.sqrt(I)*np.exp(1j*2*np.pi*np.nan_to_num(W)/par['lam'])*mask).astype(np.complex64)
    return dict(OPD=W, field=U, intensity=I.astype(np.float32), mask=mask, pitch_m=pitch, carriers=(fa, fb),
                A1=A1, A2=A2, phases=ph, mode=mode, stats=report(np.nan_to_num(W), mask, pitch), img=img, black_level=float(black))

def save_outputs(r, name, outdir):
    import scipy.io as sio, matplotlib; matplotlib.use('Agg'); import matplotlib.pyplot as plt
    os.makedirs(outdir, exist_ok=True)
    np.save(os.path.join(outdir, name+'_OPD_m.npy'), r['OPD']); np.save(os.path.join(outdir, name+'_field.npy'), r['field'])
    sio.savemat(os.path.join(outdir, name+'_recon.mat'), dict(OPD_m=np.nan_to_num(r['OPD']), mask=r['mask'].astype(np.uint8), field=r['field'],
                intensity=r['intensity'], pitch_m=r['pitch_m'], lambda_m=PAR['lam'], carrier_a=r['carriers'][0], carrier_b=r['carriers'][1]))
    fig, ax = plt.subplots(1, 4, figsize=(22, 5.2)); m = r['mask']; sl = tuple(slice(max(0, a.min()-20), a.max()+20) for a in np.nonzero(m))
    ax[0].imshow(r['img'][::4, ::4], cmap='gray'); ax[0].set_title('raw frame (/4)')
    h = ax[1].imshow(r['OPD'][sl]*1e6, cmap='viridis'); ax[1].set_title('OPD [µm]  (piston/tilt removed)'); plt.colorbar(h, ax=ax[1], fraction=.046)
    h = ax[2].imshow(np.abs(r['field'])[sl], cmap='gray'); ax[2].set_title('|U|  (normalised)'); plt.colorbar(h, ax=ax[2], fraction=.046)
    ph = np.where(m, np.angle(r['field']), np.nan)[sl]
    h = ax[3].imshow(ph, cmap='twilight', vmin=-np.pi, vmax=np.pi); ax[3].set_title('arg U [rad]  (wrapped)'); plt.colorbar(h, ax=ax[3], fraction=.046)
    s = r['stats']; fig.suptitle('%s | %s | power %+.2f D, astig %.0f nm, RMS %.0f nm, PV %.2f µm' % (name, r['mode'], s['power_D'], s['astig_rms_nm'], s['rms_nm'], s['pv_um']))
    plt.tight_layout(); plt.savefig(os.path.join(outdir, name+'_summary.png'), dpi=70); plt.close()

if __name__ == '__main__':
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('frames', nargs='*'); ap.add_argument('--out', default='results'); ap.add_argument('--calib', default=None)
    ap.add_argument('--calibrate-from', default=None, help='flat/collimated frame -> writes --calib json (one-time)')
    a = ap.parse_args()
    if a.calibrate_from:
        c = calibrate_from_flat(a.calibrate_from); json.dump(c, open(a.calib or 'calibration.json', 'w'), indent=1); print(c)
    calib = json.load(open(a.calib)) if a.calib and os.path.exists(a.calib) else None
    for f in a.frames:
        r = reconstruct(f, calib); n = os.path.splitext(os.path.basename(f))[0]
        save_outputs(r, n, a.out); print('  ', json.dumps(r['stats']))
