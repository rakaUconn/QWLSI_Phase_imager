function out = qwlsi_single_frame(frame, calibFile, outDir)
% QWLSI_SINGLE_FRAME  Wavefront (OPD) + complex field from ONE raw QWLSI / cross-grating frame,
%                     no reference capture per measurement.   (MATLAB port of qwlsi_single_frame.py)
%
%   out = qwlsi_single_frame('concave_1_img.tif', 'calibration.json', 'results')
%       reconstruct one frame.  calibFile = '' (or missing) -> nominal carriers (see note 1).
%
%   qwlsi_single_frame('--calibrate', 'ref.tif', 'calibration.json')
%       ONE-TIME system calibration from a flat / collimated frame: writes the 4 carrier numbers.
%
%   out.OPD        OPD [m] on a 3-px grid (NaN outside the pupil), piston and tilt removed
%   out.field      complex field  U = sqrt(I) .* exp(i*2*pi*OPD/lambda)
%   out.intensity  normalised intensity (zero-order lobe)       out.mask  pupil mask
%   out.pitch      grid pitch in object space [m]  (3*p/Z)      out.stats power [D], astig/coma/sph [nm], rms, PV
%
% Method
%   1. FFT; demodulate the two orthogonal carrier peaks fa, fb with a sub-bin-exact carrier
%      (no integer-bin rounding) and a circular low-pass of radius R bins  -> complex images A1, A2.
%   2. arg(A_j) = sigma*(2*pi/lambda)*[ W(x+s_j/2) - W(x-s_j/2) ],  s_j = lambda*d*f_j/p^2  [px]
%      (|s| = 2*lambda*d/Gamma  <=>  Baffou's alpha = Gamma/(4*pi*d)),   sigma = -1 (vendor sign: concave lens = bowl).
%   3. W from a masked least-squares inversion with the exact shear operator (preconditioned CG, re-wrapping passes).
%   4. Intensity from the zero-order lobe; U = sqrt(I) exp(i 2 pi W / lambda).
%
% Notes
%   1. A carrier-vector error is mathematically identical to defocus / astigmatism, so a single frame cannot find
%      it.  The carriers come from a one-time calibration (calibration.json).  Nominal carriers (2p/Gamma) were
%      off by 0.24 bin on one axis for this system -> ~3.7 um rms error (all higher-order content is still fine).
%   2. The result is the ABSOLUTE wavefront; it contains the static beam/grating wavefront (~0.3-0.4 um rms here).
%      Subtract the OPD of the reference frame (reconstructed once with this same function) to get the sample alone.
%   3. Needs base MATLAB only (R2016b+ for jsondecode/jsonencode); no toolboxes.

if nargin < 1, error('usage: see help qwlsi_single_frame'); end
P = params();

if strcmp(frame, '--calibrate')
    % qwlsi_single_frame('--calibrate', refFile, jsonOut)
    if nargin < 3, outDir = 'calibration.json'; end
    c = calibrate_from_flat(calibFile, P);
    fid = fopen(outDir, 'w'); fwrite(fid, jsonencode(c)); fclose(fid);
    fprintf('carrier a = [%.9f %.9f]  carrier b = [%.9f %.9f]  cyc/px  -> %s\n', c.fa, c.fb, outDir);
    out = c; return
end

if nargin < 2, calibFile = ''; end
if nargin < 3, outDir = 'results'; end
calib = [];
if ~isempty(calibFile) && exist(calibFile, 'file')
    calib = jsondecode(fileread(calibFile));
end

r = reconstruct(frame, calib, P);
[~, name] = fileparts(frame);
if ~exist(outDir, 'dir'), mkdir(outDir); end
OPD_m = r.OPD; field = r.field; intensity = r.intensity; mask = r.mask; pitch_m = r.pitch; lambda_m = P.lambda; %#ok<NASGU>
carrier_a = r.fa; carrier_b = r.fb; %#ok<NASGU>
matfile = fullfile(outDir, [name '_recon.mat']);
try
    save(matfile, 'OPD_m', 'field', 'intensity', 'mask', 'pitch_m', 'lambda_m', 'carrier_a', 'carrier_b', '-v7.3');
catch
    save(matfile, 'OPD_m', 'field', 'intensity', 'mask', 'pitch_m', 'lambda_m', 'carrier_a', 'carrier_b', '-v6');   % Octave
end
try, make_figure(r, name, outDir); catch ME, warning('figure skipped: %s', ME.message); end
s = r.stats;
fprintf('%s | %s | power %+.3f D, astig %.0f nm, coma %.0f nm, RMS %.0f nm, PV %.2f um\n', name, r.mode, ...
        s.power_D, s.astig_rms_nm, s.coma_rms_nm, s.rms_nm, s.pv_um);
out = r;
end

% =====================================================================================================
function P = params()
% ---- your system parameters --------------------------------------------------------------------
P.Gamma  = 16.44e-6;   % cross-grating period (grexel size) [m]
P.d      = 0.11e-3;    % grating-camera distance [m]
P.p      = 2.74e-6;    % camera pixel size [m]
P.Z      = 1;          % zoom of the relay lens
P.lambda = 650e-9;     % illumination wavelength [m]
P.R      = 695;        % low-pass radius around each peak [FFT bins of the full 4512^2 spectrum]
P.DEC    = 3;          % output grid = every 3rd pixel (window of N/3 bins; 4512/3 = 1504)
P.sigma  = -1;         % sign convention (vendor: concave lens -> bowl, centre low)
end

% =====================================================================================================
function r = reconstruct(path, calib, P)
img = double(imread(path)); N = size(img, 1); DEC = P.DEC; M = N / DEC;
if mod(N, DEC) ~= 0 || size(img, 2) ~= N, error('frame must be square with side divisible by %d', DEC); end
mu = mean(img(:)); F = fft2(img - mu);
if ~isempty(calib)
    fa = calib.fa(:).'; fb = calib.fb(:).'; mode = 'calibrated carriers';
else
    [fa, fb, th] = nominal_carriers(img, F, P); mode = sprintf('nominal carriers (theta=%.4f rad)', th);
end
A1 = baseband(F, fa, N, M, P.R); A2 = baseband(F, fb, N, M, P.R);
I0 = real(spec_window(F, [0 0], N, M, P.R)) + mu;
mask = make_mask(A1, A2);
fprintf('  %s | %s\n', path, mode);
[W, res] = solve_opd(A1, A2, fa, fb, mask, P);
fprintf('   LS residual rms [rad]: %.3f %.3f\n', res(1), res(2));
pitch = DEC * P.p / P.Z;
W(~mask) = NaN; W = W - mean(W(mask));
lo = prctile_(I0(:), 10); black = mean(I0(I0 < 1.5*lo + 1));     % dark level (zero-order lobe outside the pupil)
I = max(I0 - black, 0); I = I ./ prctile_(I(mask), 95); I(~mask) = 0;
Wz = W; Wz(~mask) = 0;
U = sqrt(I) .* exp(1i * 2*pi * Wz / P.lambda) .* mask;
r = struct('OPD', W, 'field', single(U), 'intensity', single(I), 'mask', mask, 'pitch', pitch, ...
           'fa', fa, 'fb', fb, 'A1', A1, 'A2', A2, 'mode', mode, 'img', img, 'black_level', black, 'stats', report(Wz, mask, pitch));
end

% ----------------------------------------------------------------------------------------------------
function z = spec_window(F, kc, N, M, R)
% M x M spectral window around integer bin kc = [kx ky] (signed bins), circular smooth-edged mask of radius R
% -> spatial complex image sampled every N/M px; carrier kc/N removed exactly.
m = (-M/2 : M/2-1);
sub = F(mod(kc(2) + m, N) + 1, mod(kc(1) + m, N) + 1);
[xx, yy] = meshgrid(m, m); rr = hypot(xx, yy);
w = min(max((R - rr) / (0.08 * R), 0), 1); w = 0.5 - 0.5 * cos(pi * w);
z = ifft2(ifftshift(sub .* w)) * (M*M) / (N*N);          % amplitude-preserving (ifft2 already divides by M^2)
end

function A = baseband(F, f, N, M, R)
% demodulate carrier f = [fx fy] (cycles/px, any real value) -> complex image on the DEC-px grid
DEC = N / M;
kc = round(f * N);
z = spec_window(F, kc, N, M, R);
n = (0:M-1) * DEC;
d = f - kc / N;
A = z .* exp(-2i*pi * (d(1) * n + d(2) * n.'));
end

function pk = dft_peak(img, f0)
% sub-bin carrier = maximum of |DFT| (exact for a ~flat wave; biased by strong curvature)
N = size(img, 1); x = (0:N-1).';
fun = @(p) -abs((exp(-2i*pi*p(2)*x).') * (img * exp(-2i*pi*p(1)*x)));
opts = optimset('TolX', 1e-10, 'TolFun', 1e-3, 'MaxFunEvals', 400, 'MaxIter', 400);
pk = fminsearch(fun, f0, opts);
end

function [fa, fb, th] = nominal_carriers(img, F, P)
% nominal: |f| = 2p/Gamma exactly, fb = fa rotated by -90 deg, orientation measured from the frame
N = size(img, 1); f0 = 2 * P.p / P.Gamma;
k = [0:N/2-1, -N/2:-1] / N;
[KX, KY] = meshgrid(k, k);
ann = abs(hypot(KX, KY) - f0) < 20 / N & KX > 0;
mag = abs(F) .* ann;
[~, idx] = max(mag(:)); [iy, ix] = ind2sub(size(mag), idx);
im0 = img - mean(img(:));
fa0 = dft_peak(im0, [k(ix) k(iy)]);
th_a = atan2(fa0(2), fa0(1));
fb0 = f0 * [cos(th_a - pi/2), sin(th_a - pi/2)];
fb1 = dft_peak(im0, fb0);
th_b = atan2(fb1(2), fb1(1)) + pi/2;
th = angle(exp(1i*th_a) + exp(1i*th_b));
fa = f0 * [cos(th), sin(th)]; fb = f0 * [cos(th - pi/2), sin(th - pi/2)];
end

function s = shear_px(f, P)         % shear vector [sensor px] for a carrier f [cyc/px]
s = P.lambda * P.d * f / P.p^2;
end

% ----------------------------------------------------------------------------------------------------
function mask = make_mask(A1, A2)
% pupil mask from the fringe amplitude (smoothing/erosion by convolution -> no Image Processing Toolbox)
a = min(abs(A1), abs(A2));
m = double(a > 0.15 * prctile_(a(:), 95));
m = conv2(m, disk_kernel(5), 'same') > 0.5;                 % removes specks, fills pin-holes
m = conv2(double(m), disk_kernel(4), 'same') > 0.999;       % erode by 4 grid px (stay clear of the edge)
mask = m;
end

function K = disk_kernel(rad)
[x, y] = meshgrid(-rad:rad); K = double(x.^2 + y.^2 <= rad^2); K = K / sum(K(:));
end

function v = prctile_(x, p)         % percentile without the Statistics Toolbox
x = sort(x(:)); n = numel(x); i = min(max(round(p/100 * n), 1), n); v = x(i);
end

function A = remove_piston(A, m)
A = A * exp(-1i * angle(sum(A(m))));
end

% ----------------------------------------------------------------------------------------------------
function [W, res] = solve_opd(A1, A2, fa, fb, mask, P)
% masked least-squares inversion of the two shear phases -> OPD W [m]; preconditioned CG
M = size(A1, 1); DEC = P.DEC;
k1 = [0:M/2-1, -M/2:-1] / (M * DEC);                 % cycles / px
[KX, KY] = meshgrid(k1, k1);
s1 = shear_px(fa, P); s2 = shear_px(fb, P);
H1 = 2i * sin(pi * (KX * s1(1) + KY * s1(2)));
H2 = 2i * sin(pi * (KX * s2(1) + KY * s2(2)));
c = P.sigma * 2*pi / P.lambda;
V = double(mask);
D1  = @(W)  real(ifft2(H1 .* fft2(W)));   D2  = @(W)  real(ifft2(H2 .* fft2(W)));
DT1 = @(r) -real(ifft2(H1 .* fft2(r)));   DT2 = @(r) -real(ifft2(H2 .* fft2(r)));
S = abs(H1).^2 + abs(H2).^2; Pinv = 1 ./ (S + max(S(:)) * 1e-6 + 1e-12); Pinv(1,1) = 0;
Aop = @(w) reshape(DT1(V .* D1(reshape(w, M, M))) + DT2(V .* D2(reshape(w, M, M))) + 1e-9 * reshape(w, M, M), [], 1);
Mfun = @(r) reshape(real(ifft2(Pinv .* fft2(reshape(r, M, M)))), [], 1);

Ap1 = remove_piston(A1, mask); Ap2 = remove_piston(A2, mask);
ph1 = angle(Ap1); ph2 = angle(Ap2);
passes = 2;
for it = 0:passes
    b = DT1(V .* ph1 / c) + DT2(V .* ph2 / c);
    x0 = Mfun(b(:));
    [x, ~] = pcg(Aop, b(:), 1e-7, 60, Mfun, [], x0);
    W = reshape(x, M, M);
    pred1 = c * D1(W); pred2 = c * D2(W);
    if it < passes          % re-wrap residual around the model (robust to 2*pi jumps); drop mean (= tilt)
        ph1 = pred1 + angle(Ap1 .* exp(-1i * pred1)); ph1 = ph1 - mean(ph1(mask));
        ph2 = pred2 + angle(Ap2 .* exp(-1i * pred2)); ph2 = ph2 - mean(ph2(mask));
    end
end
e1 = pred1 - ph1; e2 = pred2 - ph2;
res = [sqrt(mean(e1(mask).^2)), sqrt(mean(e2(mask).^2))];
end

% ----------------------------------------------------------------------------------------------------
function c = calibrate_from_flat(path, P)
% refine the carrier vectors from a flat / collimated frame: demodulate, fit the phase plane, update f (x3)
img = double(imread(path)); N = size(img, 1); M = N / P.DEC;
F = fft2(img - mean(img(:)));
[fa, fb] = nominal_carriers(img, F, P);
fs = {fa, fb};
for j = 1:2
    f = fs{j};
    for it = 1:3
        A = baseband(F, f, N, M, P.R);
        m = abs(A) > 0.5 * prctile_(abs(A(:)), 95);
        m = conv2(double(m), disk_kernel(5), 'same') > 0.5;
        m = conv2(double(m), disk_kernel(6), 'same') > 0.999;
        A = remove_piston(A, m); ph = angle(A);
        [X, Y] = meshgrid((0:M-1) * P.DEC);
        G = [X(m), Y(m), ones(nnz(m), 1)];
        cc = G \ ph(m);
        f = f + cc(1:2).' / (2*pi);
    end
    fs{j} = f;
end
c = struct('fa', fs{1}, 'fb', fs{2}, 'source', path, ...
           'note', 'carrier vectors [cyc/px] such that a flat wave gives zero differential-phase gradient');
end

% ----------------------------------------------------------------------------------------------------
function [z, Rm, B, m, coef] = zfit(W, mask, pitch, nmax)
[iy, ix] = find(mask); cy = mean(iy); cx = mean(ix); rad = sqrt(nnz(mask) / pi);
[X, Y] = meshgrid(1:size(W, 2), 1:size(W, 1));
rho = hypot(Y - cy, X - cx) / rad; th = atan2(Y - cy, X - cx);
names = zeros(0, 2); Bc = {};
for n = 0:nmax
    for mm = -n:2:n
        am = abs(mm); Rr = zeros(size(rho));
        for s = 0:(n - am)/2
            Rr = Rr + (-1)^s * factorial(n - s) / (factorial(s) * factorial((n + am)/2 - s) * factorial((n - am)/2 - s)) * rho.^(n - 2*s);
        end
        if mm >= 0, ang = cos(am * th); else, ang = sin(am * th); end
        Bc{end+1} = Rr .* ang * sqrt((2 - (mm == 0)) * (n + 1)); %#ok<AGROW>
        names(end+1, :) = [n mm]; %#ok<AGROW>
    end
end
m = mask & (rho <= 1);
B = zeros(numel(Bc), nnz(m));
for i = 1:numel(Bc), t = Bc{i}; B(i, :) = t(m).'; end
coef = B.' \ W(m);
z = containers.Map(); for i = 1:size(names, 1), z(sprintf('%d,%d', names(i, 1), names(i, 2))) = coef(i); end
Rm = rad * pitch;
end

function s = report(W, mask, pitch)
[z, Rm, ~, m] = zfit(W, mask, pitch, 6);
zz = @(n, mm) z(sprintf('%d,%d', n, mm));
s.power_D          = 2 * (2*sqrt(3) * zz(2, 0) / Rm^2);      % best-fit sphere curvature [D]; + = bowl (concave / diverging)
s.astig_rms_nm     = hypot(zz(2, 2), zz(2, -2)) * 1e9;
s.coma_rms_nm      = hypot(zz(3, 1), zz(3, -1)) * 1e9;
s.spherical_rms_nm = zz(4, 0) * 1e9;
s.pupil_radius_mm  = Rm * 1e3;
s.rms_nm           = std(W(m)) * 1e9;
s.pv_um            = (max(W(m)) - min(W(m))) * 1e6;
end

% ----------------------------------------------------------------------------------------------------
function make_figure(r, name, outDir)
f = figure('Visible', 'off', 'Position', [50 50 1500 380]);
m = r.mask; [iy, ix] = find(m);
sy = max(1, min(iy) - 20):min(size(m, 1), max(iy) + 20); sx = max(1, min(ix) - 20):min(size(m, 2), max(ix) + 20);
subplot(1, 4, 1); imagesc(r.img(1:4:end, 1:4:end)); axis image; colormap(gca, gray); title('raw frame (/4)');
subplot(1, 4, 2); imagesc(r.OPD(sy, sx) * 1e6); axis image; colorbar; title('OPD [\mum] (piston/tilt removed)');
subplot(1, 4, 3); imagesc(abs(r.field(sy, sx))); axis image; colorbar; colormap(gca, gray); title('|U| (normalised)');
ph = angle(r.field); ph(~m) = NaN;
subplot(1, 4, 4); imagesc(ph(sy, sx), [-pi pi]); axis image; colorbar; title('arg U [rad]');
s = r.stats;
annotation('textbox', [0 0.93 1 0.07], 'String', sprintf('%s | %s | power %+.2f D, astig %.0f nm, RMS %.0f nm, PV %.2f \\mum', name, r.mode, s.power_D, s.astig_rms_nm, s.rms_nm, s.pv_um), ...
           'EdgeColor', 'none', 'HorizontalAlignment', 'center', 'Interpreter', 'tex');
print(f, fullfile(outDir, [name '_summary.png']), '-dpng', '-r70'); close(f);
end
