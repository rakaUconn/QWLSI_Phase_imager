namespace Qwlsi.Core;

public sealed record WavefrontStats(double PowerD, double AstigRmsNm, double ComaRmsNm, double SphericalRmsNm, double PupilRadiusMm, double RmsNm, double PvUm);

/// <summary>
/// Lens report (power, astigmatism, coma, spherical, rms, PV). Uses the single OSA/ANSI implementation in <see cref="ZernikeTools"/> (order 6, the
/// same fit the Python reference reports from). Power = 4*sqrt(3)*c(j=4)/R^2 (+ = concave); astigmatism = hypot(c3, c5); coma = hypot(c7, c8);
/// spherical = c12. These are invariant to the x/y orientation flags (only the sign of sine terms changes).
/// </summary>
public static class Zernike
{
    public static WavefrontStats Report(float[] W, byte[] mask, int M, double pitch)
    {
        var f = ZernikeTools.Fit(W, mask, M, pitch, order: 6);
        double s = 0, s2 = 0, mn = double.MaxValue, mx = double.MinValue; long n = 0;
        for (int y = 0; y < M; y++) for (int x = 0; x < M; x++)
        {
            long i = (long)y * M + x; if (mask[i] == 0 || float.IsNaN(W[i])) continue;
            if (ZernikeTools.Polar(x, y, f).rho > 1) continue; double v = W[i]; s += v; s2 += v * v; n++; if (v < mn) mn = v; if (v > mx) mx = v;
        }
        double mean = s / n, std = Math.Sqrt(Math.Max(0, s2 / n - mean * mean)); var c = f.Coeff;
        return new WavefrontStats(f.Diopters(4), Math.Sqrt(c[3] * c[3] + c[5] * c[5]) * 1e9, Math.Sqrt(c[7] * c[7] + c[8] * c[8]) * 1e9, c[12] * 1e9, f.RadiusM * 1e3, std * 1e9, (mx - mn) * 1e6);
    }
}
