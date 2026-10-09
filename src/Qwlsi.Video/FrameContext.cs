using Qwlsi.Core;

namespace Qwlsi.Video;

/// <summary>One loaded frame with lazily computed derived data (Zernike removal, removed surface, raw frame, meshes). Hold one at a time.</summary>
public sealed class FrameContext
{
    public IFrameProvider Provider { get; }
    public int Index { get; }
    public string Name { get; }
    public ReconOutcome Oc { get; }
    public int M => Oc.Result.M;
    public double PitchM { get; }
    readonly VideoZernikeSpec zspec;
    ZernikeFit? fit; ZernikeRemoval? zr; float[]? removed, raw; bool rawTried;
    readonly Dictionary<(bool, MeshResolution), SurfaceMeshData> meshes = new();

    FrameContext(IFrameProvider p, int i, ReconOutcome oc, VideoZernikeSpec z) { Provider = p; Index = i; Name = p.NameOf(i); Oc = oc; PitchM = p.Par.PitchM; zspec = z; }

    public static FrameContext Create(IFrameProvider p, int i, VideoSettings s, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ReconOutcome oc; try { oc = p.Load(i, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { throw new VideoRenderException($"Frame {i + 1} ({SafeName(p, i)}): {e.Message}", e); }
        return new FrameContext(p, i, oc, s.Zernike);
    }

    static string SafeName(IFrameProvider p, int i) { try { return p.NameOf(i); } catch { return "?"; } }

    public float[] Opd => Oc.Result.Opd;
    public byte[] Mask => Oc.Result.Mask;
    /// <summary>The order-10 joint fit with the video's Zernike spec (orientation, circle) and the removal of the selected terms.</summary>
    public ZernikeRemoval Removal
    {
        get
        {
            if (zr != null) return zr;
            fit = ZernikeTools.Fit(Oc.Result.Opd, Oc.Result.Mask, M, PitchM, 10, zspec.Orientation, zspec.Circle);
            return zr = ZernikeRemoval.Make(Oc.Result.Opd, Oc.Result.Mask, fit, zspec.Selection);
        }
    }
    public ZernikeFit Fit { get { _ = Removal; return fit!; } }
    public float[] Filtered => Removal.Filtered;
    /// <summary>raw - filtered (the removed Zernike surface), NaN outside the pupil.</summary>
    public float[] Removed
    {
        get
        {
            if (removed != null) return removed; var r = Oc.Result.Opd; var f = Removal.Filtered; var o = new float[r.Length];
            for (long i = 0; i < o.Length; i++) o[i] = float.IsNaN(r[i]) || float.IsNaN(f[i]) ? float.NaN : r[i] - f[i];
            return removed = o;
        }
    }
    /// <summary>Raw frame box-averaged to the result grid (null if the provider has none).</summary>
    public float[]? Raw { get { if (!rawTried) { rawTried = true; raw = Provider.LoadRawDownsampled(Index); } return raw; } }

    public SurfaceMeshData Mesh(bool filtered, MeshResolution res)
    {
        if (meshes.TryGetValue((filtered, res), out var m)) return m;
        var src = filtered ? Filtered : Opd; var nm = new float[src.Length]; for (long i = 0; i < nm.Length; i++) nm[i] = src[i] * 1e9f;
        return meshes[(filtered, res)] = SurfaceMesh.Build(nm, M, PitchM, res);
    }

    /// <summary>Power [D] / rms [nm] / PV [um] of the OPD a panel displays: raw (Result.Stats) or the filtered / removed map (lens report of that map).</summary>
    public (double powerD, double rmsNm, double pvUm) StatsOf(PanelKind kind, bool surfaceFiltered)
    {
        bool filt = kind == PanelKind.FilteredOpd || (kind == PanelKind.Surface3D && surfaceFiltered), rem = kind == PanelKind.RemovedZernike;
        if (!filt && !rem) { var s = Oc.Result.Stats; return (s.PowerD, s.RmsNm, s.PvUm); }
        var map = filt ? Filtered : Removed; var rep = Zernike.Report(map, Mask, M, PitchM); return (rep.PowerD, rep.RmsNm, rep.PvUm);
    }
}

public sealed class VideoRenderException : Exception { public VideoRenderException(string m, Exception? inner = null) : base(m, inner) { } }
