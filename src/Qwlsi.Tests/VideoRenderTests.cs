using System.Diagnostics;
using Qwlsi.Core;
using Qwlsi.Video;

namespace Qwlsi.Tests;

/// <summary>Headless video rendering (Qwlsi.Video): layouts, range modes, units, overlays, strip plot, Zernike panels, software 3-D rasteriser, turntable, blending, job (in-memory sink), cancel, errors, timings.</summary>
[Collection("heavy")]
public class VideoRenderTests
{
    readonly GoldenFixture fx;
    public VideoRenderTests(GoldenFixture f) { fx = f; }
    static readonly string[] Names = { "concave_1_img", "concave_2_img", "convex_1_img", "convex_2_img", "concave_1-5_img" };

    sealed class Provider : IFrameProvider
    {
        readonly List<ReconOutcome> o; readonly string[] names; public int Loads; public Action<int, int>? OnLoad; public Func<int, float[]?>? Raw; public QwlsiParams Par { get; }
        public Provider(List<ReconOutcome> o, string[] names, QwlsiParams par) { this.o = o; this.names = names; Par = par; }
        public int Count => o.Count; public string NameOf(int i) => Path.GetFileName(o[i].File); public DateTime TimeOf(int i) => DateTime.UnixEpoch.AddMinutes(i);
        public ReconOutcome Load(int i, CancellationToken ct) { Loads++; OnLoad?.Invoke(i, Loads); ct.ThrowIfCancellationRequested(); return o[i]; }
        public float[]? LoadRawDownsampled(int i) => Raw?.Invoke(i);
    }

    sealed class RecSink : IVideoSink
    {
        public int Width { get; }
        public int Height { get; }
        public string Description => "recording sink";
        public List<byte[]> Frames = new(); public int Count; public bool Completed, Disposed, Keep = true; public Action<int>? OnWrite;
        public RecSink(int w, int h) { Width = w; Height = h; }
        public void WriteFrame(ReadOnlySpan<byte> bgra) { if (bgra.Length != Width * Height * 4) throw new InvalidOperationException("wrong frame size"); Count++; if (Keep) Frames.Add(bgra.ToArray()); OnWrite?.Invoke(Count); }
        public void Complete() { Completed = true; }
        public void Dispose() { Disposed = true; }
    }

    sealed class SyncProgress<T> : IProgress<T> { readonly Action<T> a; public SyncProgress(Action<T> a) { this.a = a; } public void Report(T v) => a(v); }

    Provider MakeProvider(int n = 5)
    {
        var l = new List<ReconOutcome>(); foreach (var nm in Names.Take(n)) l.Add(new ReconOutcome { File = Path.Combine(fx.Root, nm + ".tif"), Mode = "without reference", Result = fx.Recon(nm), CarriersUsed = fx.Calib, Seconds = 1 });
        return new Provider(l, Names, fx.Par);
    }

    static VideoSettings One(PanelKind k = PanelKind.Opd, ResolutionPreset r = ResolutionPreset.P720) => new() { Panels = new() { new PanelSpec(k) }, Resolution = r };

    // ------------------------------------------------------------------------------------------------ helpers (no data)
    [Fact]
    public void Frame_order_natural_by_time_and_range()
    {
        var items = new[] { ("f10.tif", 3), ("f2.tif", 1), ("F1.tif", 2), ("f02b.tif", 0) };
        var byName = FrameList.Order(items, t => t.Item1, t => DateTime.UnixEpoch.AddMinutes(t.Item2), FrameOrder.ByName).Select(t => t.Item1).ToArray();
        Assert.Equal(new[] { "F1.tif", "f2.tif", "f02b.tif", "f10.tif" }, byName);
        var byTime = FrameList.Order(items, t => t.Item1, t => DateTime.UnixEpoch.AddMinutes(t.Item2), FrameOrder.ByTime).Select(t => t.Item1).ToArray();
        Assert.Equal(new[] { "f02b.tif", "f2.tif", "F1.tif", "f10.tif" }, byTime);
        var l = Enumerable.Range(1, 10).ToList(); Assert.Equal(new[] { 3, 4, 5 }, FrameList.Range(l, 3, 5)); Assert.Equal(10, FrameList.Range(l, 1, 0).Count); Assert.Equal(new[] { 9, 10 }, FrameList.Range(l, 9, 99));
    }

    [Fact]
    public void Output_frame_counts_fps_clamp_and_turntable_azimuth()
    {
        var s = new VideoSettings { BlendSteps = 0 }; Assert.Equal(5, s.OutputFrames(5)); s.BlendSteps = 2; Assert.Equal(5 + 4 * 2, s.OutputFrames(5)); s.Turntable = true; s.TurntableSeconds = 2; s.Fps = 10; Assert.Equal(20, s.OutputFrames(5));
        s.Fps = 500; Assert.Equal(60, s.FpsClamped); s.Fps = 0; Assert.Equal(1, s.FpsClamped); s.Fps = 10; s.Surface.AzimuthDeg = -45; Assert.Equal(-45, s.TurntableAzimuth(0, 20)); Assert.Equal(-45 + 360.0 * 5 / 20, s.TurntableAzimuth(5, 20), 9);
    }

    // ------------------------------------------------------------------------------------------------ sizes / layout
    [Fact]
    public void Output_size_per_resolution_preset_and_native_crop_is_even()
    {
        var p = MakeProvider(); var s = One(); var plan = ColorRangePlan.Build(p, s);
        var l720 = VideoComposer.Layout(s, plan); Assert.Equal((1280, 720), (l720.Width, l720.Height));
        var r = VideoComposer.RenderFrame(p, s, plan, 0); Assert.Equal((1280, 720), (r.Width, r.Height)); Assert.Equal(1280 * 720 * 4, r.Bgra.Length);
        s.Resolution = ResolutionPreset.P1080; Assert.Equal((1920, 1080), (VideoComposer.Layout(s, plan).Width, VideoComposer.Layout(s, plan).Height));
        s.Resolution = ResolutionPreset.NativeCropped; var ln = VideoComposer.Layout(s, plan); Assert.True(ln.Width % 2 == 0 && ln.Height % 2 == 0);
        Assert.True(plan.CropW < fx.Par.M && plan.CropW > 800, $"crop {plan.CropW}x{plan.CropH}");     // pupil ~1030 px + margins, not the whole 1504 grid
        var rn = VideoComposer.RenderFrame(p, s, plan, 0); Assert.Equal((ln.Width, ln.Height), (rn.Width, rn.Height)); Assert.Equal(ln.Width * ln.Height * 4, rn.Bgra.Length);
        Assert.True(ln.Images[0].Width >= plan.CropW && ln.Images[0].Height >= plan.CropH, "native image area holds the crop 1:1");
        s.StripPlot = true; s.Colorbar = false; var l2 = VideoComposer.Layout(s, plan); Assert.True(l2.Strip.Height > 0 && l2.Width % 2 == 0 && l2.Height % 2 == 0);
    }

    [Fact]
    public void Layouts_for_one_to_four_panels_are_non_empty_and_distinct()
    {
        var p = MakeProvider(3); var kinds = new[] { PanelKind.Opd, PanelKind.FilteredOpd, PanelKind.Intensity, PanelKind.WrappedPhase };
        for (int n = 1; n <= 4; n++)
        {
            var s = One(); s.Panels = kinds.Take(n).Select(k => new PanelSpec(k)).ToList(); var plan = ColorRangePlan.Build(p, s); var L = VideoComposer.Layout(s, plan); var rf = VideoComposer.RenderFrame(p, s, plan, 0);
            Assert.Equal(n, L.Panels.Length); Assert.Equal((1280, 720), (rf.Width, rf.Height));
            if (n == 3) { Assert.Equal(1280, L.Panels[0].Width); Assert.True(L.Panels[1].Y > L.Panels[0].Y && L.Panels[2].X > L.Panels[1].X); }
            if (n == 2) Assert.True(L.Panels[1].X > L.Panels[0].X && L.Panels[1].Y == L.Panels[0].Y);
            var sig = new HashSet<long>();
            for (int i = 0; i < n; i++)
            {
                var r = L.Images[i]; Assert.True(r.Width > 50 && r.Height > 50 && r.Right <= L.Width && r.Bottom <= L.Height); var colors = new HashSet<uint>(); long h = 17;
                for (int y = r.Y; y < r.Bottom; y += 3) for (int x = r.X; x < r.Right; x += 3) { int o = (y * L.Width + x) * 4; uint c = (uint)(rf.Bgra[o] | rf.Bgra[o + 1] << 8 | rf.Bgra[o + 2] << 16); colors.Add(c); h = h * 31 + c; }
                Assert.True(colors.Count > 30, $"panel {i} ({kinds[i]}) looks empty: {colors.Count} colours"); Assert.True(sig.Add(h), $"panel {i} identical to another panel");
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ colour ranges and units
    static (double lo, double hi) FrameMinMaxNm(ReconOutcome o) { double lo = double.MaxValue, hi = double.MinValue; foreach (var v in o.Result.Opd) { if (float.IsNaN(v)) continue; lo = Math.Min(lo, v * 1e9); hi = Math.Max(hi, v * 1e9); } return (lo, hi); }

    [Fact]
    public void Range_modes_fixed_auto_percentile_and_manual()
    {
        var p = MakeProvider(); var s = One(); var plan = ColorRangePlan.Build(p, s); var all = Enumerable.Range(0, p.Count).Select(i => FrameMinMaxNm(p.Load(i, default))).ToArray();
        // Fixed (default): the same range for every frame = min / max over all frames
        var rs = Enumerable.Range(0, p.Count).Select(i => VideoComposer.RenderFrame(p, s, plan, i)).ToArray();
        Assert.All(rs, r => { Assert.Equal(rs[0].RangeLo[0], r.RangeLo[0]); Assert.Equal(rs[0].RangeHi[0], r.RangeHi[0]); });
        Assert.Equal(all.Min(a => a.lo), rs[0].RangeLo[0], 2); Assert.Equal(all.Max(a => a.hi), rs[0].RangeHi[0], 2);
        // Auto per frame: differs between a concave and a convex frame and equals that frame's min / max
        s.Range = RangeMode.AutoPerFrame; var a0 = VideoComposer.RenderFrame(p, s, plan, 0); var a2 = VideoComposer.RenderFrame(p, s, plan, 2);
        Assert.NotEqual(a0.RangeLo[0], a2.RangeLo[0]); Assert.Equal(all[0].lo, a0.RangeLo[0], 2); Assert.Equal(all[2].hi, a2.RangeHi[0], 2);
        // Percentile clipped: inside min..max of the frame, tighter than min..max, 1/99 like the app's auto range
        s.Range = RangeMode.PercentileClipped; var pc = VideoComposer.RenderFrame(p, s, plan, 0); Assert.True(pc.RangeLo[0] >= all[0].lo - 1e-3 && pc.RangeHi[0] <= all[0].hi + 1e-3 && pc.RangeLo[0] > all[0].lo && pc.RangeHi[0] < all[0].hi);
        var map = p.Load(0, default).Result.Opd.Select(v => v * 1e9f).ToArray(); var (el, eh) = Colormap.AutoRange(map, 1, 99); Assert.Equal(el, pc.RangeLo[0], 1); Assert.Equal(eh, pc.RangeHi[0], 1);
        s.PctLo = 10; s.PctHi = 90; var pc2 = VideoComposer.RenderFrame(p, s, plan, 0); Assert.True(pc2.RangeLo[0] > pc.RangeLo[0] && pc2.RangeHi[0] < pc.RangeHi[0]);
        // manual fixed range (display unit)
        s.Range = RangeMode.FixedAcrossFrames; s.ManualMin = -1000; s.ManualMax = 2500; var plan2 = ColorRangePlan.Build(p, s); var m = VideoComposer.RenderFrame(p, s, plan2, 1); Assert.Equal((-1000.0, 2500.0), (m.RangeLo[0], m.RangeHi[0]));
    }

    [Fact]
    public void Units_nm_um_waves_scale_the_displayed_range_and_label()
    {
        var p = MakeProvider(2); var s = One(); s.Range = RangeMode.AutoPerFrame; var plan = ColorRangePlan.Build(p, s); double lam = fx.Par.Lambda;
        var nm = VideoComposer.RenderFrame(p, s, plan, 0); s.Unit = Units.Um; var um = VideoComposer.RenderFrame(p, s, plan, 0); s.Unit = Units.Waves; var wv = VideoComposer.RenderFrame(p, s, plan, 0);
        Assert.Equal("nm", nm.Units[0]); Assert.Equal("µm", um.Units[0]); Assert.Equal("waves", wv.Units[0]);
        Assert.Equal(nm.RangeLo[0] / 1000, um.RangeLo[0], 3); Assert.Equal(nm.RangeHi[0] / 1000, um.RangeHi[0], 3); Assert.Equal(nm.RangeLo[0] * 1e-9 / lam, wv.RangeLo[0], 3); Assert.Equal(nm.RangeHi[0] * 1e-9 / lam, wv.RangeHi[0], 3);
        Assert.NotEqual(nm.Bgra, um.Bgra);               // the colour bar labels differ
        s.Colorbar = false; s.Unit = Units.Nm; var nb = VideoComposer.RenderFrame(p, s, plan, 0); Assert.NotEqual(nm.Bgra, nb.Bgra);
    }

    // ------------------------------------------------------------------------------------------------ overlays / strip plot
    [Fact]
    public void Overlays_name_stats_and_strip_plot_marker()
    {
        var p = MakeProvider(); var s = One(); s.OverlayName = s.OverlayStats = true; var plan = ColorRangePlan.Build(p, s); var on = VideoComposer.RenderFrame(p, s, plan, 0);
        Assert.Contains("concave_1_img.tif", on.HeaderLeft); Assert.Contains("1 / 5", on.HeaderLeft); Assert.Contains("power +0.711 D", on.HeaderRight); Assert.Contains("rms", on.HeaderRight); Assert.Contains("PV", on.HeaderRight);
        s.OverlayName = s.OverlayStats = false; var off = VideoComposer.RenderFrame(p, s, plan, 0); Assert.Equal("", off.HeaderLeft); Assert.Equal(0, VideoComposer.Layout(s, plan).Header.Height); Assert.NotEqual(on.Bgra, off.Bgra);
        // strip plot: series = the frames' values; the marker moves with the frame number
        s.OverlayName = s.OverlayStats = true; s.StripPlot = true; s.Strip = StripValue.PowerD; var sp = ColorRangePlan.Build(p, s); Assert.Equal(5, sp.Strip.Length);
        for (int i = 0; i < 5; i++) Assert.Equal(p.Load(i, default).Result.Stats.PowerD, sp.Strip[i], 4);
        var L = VideoComposer.Layout(s, sp); var xs = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            var rf = VideoComposer.RenderFrame(p, s, sp, i); double sx = 0; int c = 0; var r = L.StripPlot;
            for (int y = r.Y; y < r.Bottom; y++) for (int x = 0; x < L.Width; x++) { int o = (y * L.Width + x) * 4; if (rf.Bgra[o] == 64 && rf.Bgra[o + 1] == 64 && rf.Bgra[o + 2] == 255 && rf.Bgra[o + 3] == 255) { sx += x; c++; } }
            Assert.True(c > 20, $"marker not found for frame {i + 1}"); xs.Add(sx / c);
        }
        for (int i = 1; i < 5; i++) Assert.True(xs[i] > xs[i - 1] + 20, $"marker x {string.Join(",", xs.Select(v => v.ToString("0")))}");
        Assert.InRange(xs[0], L.StripPlot.X - 1.5, L.StripPlot.X + 1.5); Assert.InRange(xs[4], L.StripPlot.Right - 1.5, L.StripPlot.Right + 1.5);        // first / last frame at the plot edges (2 px marker)
        // Zernike term strip = order-10 coefficient [nm] of the raw OPD
        s.Strip = StripValue.ZernikeTerm; s.StripTerm = 4; var zp = ColorRangePlan.Build(p, s); var oc = p.Load(1, default); var fit = ZernikeTools.Fit(oc.Result.Opd, oc.Result.Mask, oc.Result.M, fx.Par.PitchM, 10, new ZernikeOrientation());
        Assert.Equal(fit.Coeff[4] * 1e9, zp.Strip[1], 3); Assert.Contains("defocus", zp.StripLabel);
    }

    // ------------------------------------------------------------------------------------------------ Zernike panels
    [Fact]
    public void Filtered_plus_removed_equals_raw_and_the_spec_is_respected()
    {
        var p = MakeProvider(2); var s = One(); s.Zernike.Selection = ZernikeTools.Preset("+defocus"); var fc = FrameContext.Create(p, 0, s, default); var raw = fc.Opd;
        double worst = 0; for (long i = 0; i < raw.LongLength; i++) { if (float.IsNaN(raw[i])) { Assert.True(float.IsNaN(fc.Filtered[i]) && float.IsNaN(fc.Removed[i])); continue; } worst = Math.Max(worst, Math.Abs(fc.Filtered[i] + (double)fc.Removed[i] - raw[i])); }
        Assert.True(worst < 5e-13, $"filtered + removed - raw = {worst} m");
        Assert.True(fc.Removal.RmsAfterM < 0.2 * fc.Removal.RmsBeforeM, "piston+tilt+defocus removal reduces the rms of a lens frame a lot");
        s.Zernike.Selection = ZernikeTools.Preset("none"); var none = FrameContext.Create(p, 0, s, default); Assert.Equal(0, none.Removed.Where(v => !float.IsNaN(v)).Max(Math.Abs)); Assert.Equal(raw, none.Filtered);
        // per-frame circle vs common circle (smaller, shifted circle) give different fits
        var per = fc.Fit; Assert.False(per.CommonCircle); s.Zernike.Selection = ZernikeTools.Preset("piston+tilt"); s.Zernike.Circle = new ZernikeCircle(per.Cx + 25, per.Cy - 10, per.RadiusPx * 0.9);
        var com = FrameContext.Create(p, 0, s, default); Assert.True(com.Fit.CommonCircle); Assert.Equal(per.Cx + 25, com.Fit.Cx, 9); Assert.True(Math.Abs(com.Fit.Coeff[4] - per.Coeff[4]) > 1e-12 || Math.Abs(com.Fit.Coeff[7] - per.Coeff[7]) > 1e-12);
        Assert.NotEqual(com.Fit.FitPixels, per.FitPixels);
        // the panels honour the spec: removed-surface panel of 'none' is empty-range, of +defocus is not
        s.Zernike.Circle = null; s.Zernike.Selection = ZernikeTools.Preset("+defocus"); s.Panels = new() { new PanelSpec(PanelKind.RemovedZernike), new PanelSpec(PanelKind.FilteredOpd) }; s.Range = RangeMode.AutoPerFrame; var plan = ColorRangePlan.Build(p, s);
        var rf = VideoComposer.RenderFrame(p, s, plan, 0); Assert.True(rf.RangeHi[0] - rf.RangeLo[0] > 1000, "removed defocus surface spans > 1 um"); Assert.True(rf.RangeHi[1] - rf.RangeLo[1] < rf.RangeHi[0] - rf.RangeLo[0]);
        s.Zernike.Selection = ZernikeTools.Preset("none"); var rn = VideoComposer.RenderFrame(p, s, plan, 0); Assert.True(rn.RangeHi[0] - rn.RangeLo[0] <= 1.0 + 1e-6, "nothing removed -> removed panel range collapses to the minimum 1-unit span");
    }

    [Fact]
    public void Raw_intensity_phase_panels_and_missing_raw()
    {
        var p = MakeProvider(2); p.Raw = i => p.Load(i, default).Result.Intensity.Select(v => v * 1000f).ToArray(); var s = One(PanelKind.RawFrame); var plan = ColorRangePlan.Build(p, s); var r = VideoComposer.RenderFrame(p, s, plan, 0); Assert.Equal("counts", r.Units[0]); Assert.True(r.RangeHi[0] > 100);
        s.Panels = new() { new PanelSpec(PanelKind.WrappedPhase), new PanelSpec(PanelKind.Intensity) }; plan = ColorRangePlan.Build(p, s); r = VideoComposer.RenderFrame(p, s, plan, 0); Assert.Equal((-Math.PI, Math.PI), (r.RangeLo[0], r.RangeHi[0])); Assert.Equal("rad", r.Units[0]); Assert.Equal("relative", r.Units[1]);
        p.Raw = null; s.Panels = new() { new PanelSpec(PanelKind.RawFrame) }; plan = ColorRangePlan.Build(p, s); r = VideoComposer.RenderFrame(p, s, plan, 0); Assert.Equal(1280 * 720 * 4, r.Bgra.Length);           // "not available" instead of an exception
    }

    // ------------------------------------------------------------------------------------------------ software 3-D rasteriser
    static SurfaceMeshData Paraboloid(int M = 128, double pitch = 8e-6)
    {
        var map = new float[M * M]; double c = (M - 1) / 2.0, R = 0.45 * M;
        for (int y = 0; y < M; y++) for (int x = 0; x < M; x++) { double r = Math.Sqrt((x - c) * (x - c) + (y - c) * (y - c)); map[y * M + x] = r > R ? float.NaN : (float)(4000.0 * (r / R) * (r / R) - 2000.0); }
        return SurfaceMesh.Build(map, M, pitch, MeshResolution.Full);
    }

    [Fact]
    public void Software_rasteriser_colours_holes_determinism_and_azimuth()
    {
        var mesh = Paraboloid(); var v = new SurfaceView { ElevationDeg = 90, AzimuthDeg = 0, Exaggeration = 1000, Cmap = ColormapKind.Viridis, LoNm = -2000, HiNm = 2000, Shading = false, ZCenterNm = 0 };
        int W = 240, H = 240; var px = SoftwareSurface.Render(mesh, v, W, H, out var cam); var lut = Colormap.Lut(ColormapKind.Viridis);
        uint Px(int x, int y) => px[y * W + x]; static int Ch(uint c, int sh) => (int)((c >> sh) & 255);
        uint centre = Px(W / 2, H / 2); uint lo = lut[0]; Assert.True(Math.Abs(Ch(centre, 16) - Ch(lo, 16)) <= 6 && Math.Abs(Ch(centre, 8) - Ch(lo, 8)) <= 6 && Math.Abs(Ch(centre, 0) - Ch(lo, 0)) <= 6, $"centre {centre:X8} vs lut[0] {lo:X8}");
        // near the rim the value is close to +2000 nm -> top of the colour map
        var (rx, ry) = cam.Project(0.9 * (cam.XMax), 0, 0); uint rim = Px((int)rx, (int)ry); uint hi = lut[255]; Assert.True(Math.Abs(Ch(rim, 8) - Ch(hi, 8)) < 60 && Ch(rim, 16) > Ch(centre, 16), $"rim {rim:X8} vs lut[255] {hi:X8}");
        Assert.Equal(0xFF101010u, Px(3, 3)); Assert.Equal(0xFF101010u, Px(W - 4, H - 4));                        // holes outside the pupil stay background
        var px2 = SoftwareSurface.Render(mesh, v, W, H, out _); Assert.Equal(px, px2);                           // deterministic
        v.ElevationDeg = 30; v.AzimuthDeg = -45; var a = SoftwareSurface.Render(mesh, v, W, H, out _); v.AzimuthDeg = 315; var b = SoftwareSurface.Render(mesh, v, W, H, out _); v.AzimuthDeg = -45 + 720; var c2 = SoftwareSurface.Render(mesh, v, W, H, out _);
        Assert.Equal(a, b); Assert.Equal(a, c2);                                                                   // render(az) == render(az + 360 k), bit identical
        v.AzimuthDeg = -15; var d = SoftwareSurface.Render(mesh, v, W, H, out _); Assert.NotEqual(a, d);
        v.Shading = true; var sh = SoftwareSurface.Render(mesh, v, W, H, out _); Assert.NotEqual(a, sh);
        // hidden surface removal: a tall spike in front must hide the surface behind it
        var spike = new float[64 * 64]; for (int i = 0; i < spike.Length; i++) spike[i] = 0; for (int y = 28; y < 36; y++) for (int x = 28; x < 36; x++) spike[y * 64 + x] = 5000f; var sm = SurfaceMesh.Build(spike, 64, 8e-6, MeshResolution.Full);
        var sv = new SurfaceView { ElevationDeg = 10, AzimuthDeg = 0, Exaggeration = 200, Cmap = ColormapKind.Gray, LoNm = 0, HiNm = 5000, Shading = false, ZCenterNm = 2500 }; var sp = SoftwareSurface.Render(sm, sv, 200, 200, out var scam);
        var (tx, ty) = scam.Project(sm.Position(32, 32).x, sm.Position(32, 32).y, 2500 * 1e-6 * 200); uint top = sp[(int)ty * 200 + (int)tx]; Assert.True(Ch(top, 8) > 200, $"spike top should be bright (value 5000 -> white): {top:X8}");
    }

    [Fact]
    public void Software_rasteriser_speed_256_and_512_mesh()
    {
        var p = MakeProvider(1); var fc = FrameContext.Create(p, 0, One(), default); foreach (var res in new[] { MeshResolution.N256, MeshResolution.N512 })
        {
            var mesh = fc.Mesh(false, res); var v = new SurfaceView { Exaggeration = 500, LoNm = -3000, HiNm = 3000 }; SoftwareSurface.Render(mesh, v, 960, 540, out _); var sw = Stopwatch.StartNew(); const int n = 5; for (int i = 0; i < n; i++) SoftwareSurface.Render(mesh, v, 960, 540, out _);
            double ms = sw.Elapsed.TotalMilliseconds / n; Console.WriteLine($"TIMING software 3-D {mesh.N}x{mesh.N} mesh into 960x540 (2x supersampled): {ms:F0} ms"); Assert.True(ms < (res == MeshResolution.N256 ? 200 : 800), $"{ms} ms");
        }
    }

    // ------------------------------------------------------------------------------------------------ turntable, blend, job
    [Fact]
    public void Turntable_frame_count_loop_closes_and_job_renders_it()
    {
        var p = MakeProvider(2); var s = One(PanelKind.Surface3D); s.Turntable = true; s.TurntableSeconds = 1.2; s.Fps = 10; s.TurntableFrame = 1; s.Surface.ElevationDeg = 35; s.Surface.AzimuthDeg = 20; var plan = ColorRangePlan.Build(p, s);
        Assert.True(plan.Turntable); Assert.Equal(12, s.OutputFrames(2)); var fc = FrameContext.Create(p, 1, s, default);
        var k0 = VideoComposer.Render(fc, s, plan, 1, 1, s.TurntableAzimuth(0, 12)); var kEnd = VideoComposer.Render(fc, s, plan, 1, 1, s.TurntableAzimuth(12, 12)); var k1 = VideoComposer.Render(fc, s, plan, 1, 1, s.TurntableAzimuth(1, 12)); var k6 = VideoComposer.Render(fc, s, plan, 1, 1, s.TurntableAzimuth(6, 12));
        Assert.Equal(k0.Bgra, kEnd.Bgra); Assert.NotEqual(k0.Bgra, k1.Bgra); Assert.NotEqual(k0.Bgra, k6.Bgra); Assert.Equal(20.0 + 180.0, k6.AzimuthDeg, 6);
        var rs = new List<RecSink>(); var sum = VideoJob.Run(p, s, (w, h) => { var r = new RecSink(w, h); rs.Add(r); return r; }, null, default, plan);
        Assert.Equal(12, sum.Frames); Assert.Equal(12, rs[0].Count); Assert.True(rs[0].Completed && rs[0].Disposed); Assert.Equal(k0.Bgra, rs[0].Frames[0]); Assert.Equal(k6.Bgra, rs[0].Frames[6]);
    }

    [Fact]
    public void Blend_zero_is_plain_frames_and_blend_n_inserts_n_frames()
    {
        var p = MakeProvider(3); var s = One(); var plan = ColorRangePlan.Build(p, s); var plain = Enumerable.Range(0, 3).Select(i => VideoComposer.RenderFrame(p, s, plan, i).Bgra).ToArray();
        var r0 = new RecSink(0, 0); VideoJob.Run(p, s, (w, h) => r0 = new RecSink(w, h), null, default, plan); Assert.Equal(3, r0.Count); for (int i = 0; i < 3; i++) Assert.Equal(plain[i], r0.Frames[i]);
        s.BlendSteps = 2; var r2 = new RecSink(0, 0); VideoJob.Run(p, s, (w, h) => r2 = new RecSink(w, h), null, default, plan); Assert.Equal(3 + 2 * 2, r2.Count);
        Assert.Equal(plain[0], r2.Frames[0]); Assert.Equal(plain[1], r2.Frames[3]); Assert.Equal(plain[2], r2.Frames[6]);
        for (int k = 0; k < plain[0].Length; k += 997) { Assert.InRange(r2.Frames[1][k], Math.Min(plain[0][k], plain[1][k]), Math.Max(plain[0][k], plain[1][k])); int exp = (int)Math.Round(plain[0][k] * (2 / 3.0) + plain[1][k] * (1 / 3.0)); Assert.True(Math.Abs(r2.Frames[1][k] - exp) <= 1, $"blend byte {k}: {r2.Frames[1][k]} vs {exp}"); }
    }

    [Theory]
    [InlineData(ResolutionPreset.P720, 1280, 720)]
    [InlineData(ResolutionPreset.P1080, 1920, 1080)]
    public void Job_renders_exactly_five_frames_of_the_expected_size(ResolutionPreset res, int w, int h)
    {
        var p = MakeProvider(); var s = One(PanelKind.Opd, res); s.StripPlot = true; RecSink? sink = null; var phases = new List<string>();
        var sum = VideoJob.Run(p, s, (ww, hh) => sink = new RecSink(ww, hh) { Keep = false }, new SyncProgress<VideoProgress>(v => phases.Add(v.Phase)), default);
        Assert.Equal(5, sum.Frames); Assert.Equal((w, h), (sum.Width, sum.Height)); Assert.Equal(5, sink!.Count); Assert.Equal((w, h), (sink.Width, sink.Height)); Assert.True(sink.Completed && sink.Disposed);
        Assert.Contains("Analysing frames", phases); Assert.Contains("Rendering", phases); Assert.Equal(5, phases.Count(x => x == "Rendering"));
    }

    [Fact]
    public async Task Job_runs_in_the_background_and_reports_progress()
    {
        var p = MakeProvider(3); var s = One(); RecSink? sink = null; var last = 0; var sum = await VideoJob.RunAsync(p, s, (w, h) => sink = new RecSink(w, h) { Keep = false }, new SyncProgress<VideoProgress>(v => { if (v.Phase == "Rendering") last = v.Done; }));
        Assert.Equal(3, sum.Frames); Assert.Equal(3, last); Assert.True(sink!.Completed);
    }

    [Fact]
    public void Cancel_mid_run_leaves_the_sink_uncompleted_and_stops_promptly()
    {
        var p = MakeProvider(); var s = One(); var plan = ColorRangePlan.Build(p, s); using var cts = new CancellationTokenSource(); RecSink? sink = null; var sw = new Stopwatch(); long atCancel = 0;
        sink = null; Assert.Throws<OperationCanceledException>(() => VideoJob.Run(p, s, (w, h) => { sink = new RecSink(w, h) { Keep = false, OnWrite = n => { if (n == 2) { cts.Cancel(); sw.Restart(); } } }; return sink; }, null, cts.Token, plan));
        atCancel = sw.ElapsedMilliseconds; Assert.False(sink!.Completed); Assert.True(sink.Disposed); Assert.Equal(2, sink.Count); Assert.True(atCancel < 1500, $"stopped {atCancel} ms after cancel");
        // cancel while analysing
        using var c2 = new CancellationTokenSource(); var p3 = MakeProvider(); p3.OnLoad = (i, n) => { if (n == 2) c2.Cancel(); }; RecSink? s2 = null;
        Assert.ThrowsAny<OperationCanceledException>(() => VideoJob.Run(p3, s, (w, h) => s2 = new RecSink(w, h), null, c2.Token)); Assert.Null(s2);      // cancelled during the analysis pass: no sink was created
    }

    [Fact]
    public void A_failing_frame_raises_a_clear_error_in_both_passes()
    {
        var s = One(); var p = MakeProvider(); p.OnLoad = (i, n) => { if (i == 2) throw new InvalidOperationException("disk cache unreadable"); };
        var e1 = Assert.Throws<VideoRenderException>(() => VideoJob.Run(p, s, (w, h) => new RecSink(w, h))); Assert.Contains("Frame 3", e1.Message); Assert.Contains("convex_1_img.tif", e1.Message); Assert.Contains("disk cache unreadable", e1.Message);
        // only the render pass fails (the analysis pass was given): the sink is created, not completed, and disposed
        var p2 = MakeProvider(); var plan = ColorRangePlan.Build(p2, s); p2.OnLoad = (i, n) => { if (i == 3) throw new InvalidOperationException("boom"); }; RecSink? sink = null;
        var e2 = Assert.Throws<VideoRenderException>(() => VideoJob.Run(p2, s, (w, h) => sink = new RecSink(w, h), null, default, plan)); Assert.Contains("Frame 4", e2.Message); Assert.False(sink!.Completed); Assert.True(sink.Disposed); Assert.Equal(3, sink.Count);
        Assert.Throws<VideoRenderException>(() => VideoJob.Run(new Provider(new List<ReconOutcome>(), Names, fx.Par), s, (w, h) => new RecSink(w, h)));
    }

    // ------------------------------------------------------------------------------------------------ estimate
    [Fact]
    public void Estimate_duration_size_and_text()
    {
        var p = MakeProvider(); var s = One(PanelKind.Opd, ResolutionPreset.P1080); s.Fps = 10; var plan = ColorRangePlan.Build(p, s); var o = new VideoEncodeOptions { BitsPerPixel = 0.1 };
        var q = VideoEstimate.Quick(s, 5, VideoFormat.Mp4, o); Assert.Equal((1920, 1080, 5), (q.Width, q.Height, q.Frames)); Assert.Equal(0.5, q.Seconds, 9); Assert.Equal((long)(0.1 * 1920 * 1080 * 5 / 8), q.Bytes); Assert.Contains("5 frames, 0.5 s at 10 fps, 1920x1080, about", q.Text);
        var m = VideoEstimate.Measured(p, s, plan, VideoFormat.PngSequence, o); Assert.True(m.Measured && m.Bytes > 5 * 20_000, m.Text); var j = VideoEstimate.Measured(p, s, plan, VideoFormat.Avi, o); Assert.True(j.Measured && j.Bytes > 5 * 10_000 && j.Bytes < m.Bytes, $"{j.Text} / {m.Text}");
        s.Blend(2); Assert.Equal(13, VideoEstimate.Quick(s, 5, VideoFormat.Mp4).Frames); s.Turntable = true; s.TurntableSeconds = 3; Assert.Equal(30, VideoEstimate.Quick(s, 5, VideoFormat.Gif).Frames);
        Console.WriteLine("ESTIMATE " + m.Text + " | " + j.Text + " | " + q.Text);
    }

    /// <summary>Visual check: set QWLSI_VIDEO_DUMP=folder to write sample frames as PNG (no-op otherwise).</summary>
    [Fact]
    public void Dump_sample_frames_when_requested()
    {
        var dir = Environment.GetEnvironmentVariable("QWLSI_VIDEO_DUMP"); if (string.IsNullOrEmpty(dir)) return; Directory.CreateDirectory(dir); var p = MakeProvider();
        void Save(string name, VideoSettings s) { var plan = ColorRangePlan.Build(p, s); var rf = VideoComposer.RenderFrame(p, s, plan, 2); using var bmp = new System.Drawing.Bitmap(rf.Width, rf.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb); var d = bmp.LockBits(new System.Drawing.Rectangle(0, 0, rf.Width, rf.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb); for (int y = 0; y < rf.Height; y++) System.Runtime.InteropServices.Marshal.Copy(rf.Bgra, y * rf.Width * 4, d.Scan0 + y * d.Stride, rf.Width * 4); bmp.UnlockBits(d); bmp.Save(Path.Combine(dir, name + ".png")); }
        var s1 = One(PanelKind.Opd, ResolutionPreset.P1080); s1.StripPlot = true; Save("one_opd_strip", s1);
        var s4 = One(PanelKind.Opd, ResolutionPreset.P1080); s4.Panels = new[] { PanelKind.Opd, PanelKind.FilteredOpd, PanelKind.RemovedZernike, PanelKind.Surface3D }.Select(k => new PanelSpec(k)).ToList(); s4.Zernike.Selection = ZernikeTools.Preset("+astigmatism"); s4.Unit = Units.Um; s4.StripPlot = true; s4.Strip = StripValue.RmsNm; Save("four_panels", s4);
        var s3 = One(PanelKind.Opd, ResolutionPreset.P720); s3.Panels = new[] { PanelKind.Surface3D, PanelKind.Intensity, PanelKind.WrappedPhase }.Select(k => new PanelSpec(k)).ToList(); Save("three_panels_720", s3);
        var sn = One(PanelKind.FilteredOpd, ResolutionPreset.NativeCropped); sn.Cmap = ColormapKind.Jet; Save("native_filtered", sn);
    }

    // ------------------------------------------------------------------------------------------------ timings and memory
    [Fact]
    public void Timings_per_frame_and_peak_memory_of_a_five_frame_1080p_four_panel_render()
    {
        var p = MakeProvider(); foreach (var cfg in new[] { ("1 panel OPD", new[] { PanelKind.Opd }), ("4 panels OPD/filtered/intensity/phase", new[] { PanelKind.Opd, PanelKind.FilteredOpd, PanelKind.Intensity, PanelKind.WrappedPhase }), ("3-D surface 256", new[] { PanelKind.Surface3D }), ("OPD + 3-D", new[] { PanelKind.Opd, PanelKind.Surface3D }) })
        {
            var s = One(PanelKind.Opd, ResolutionPreset.P1080); s.Panels = cfg.Item2.Select(k => new PanelSpec(k)).ToList(); var plan = ColorRangePlan.Build(p, s); VideoComposer.RenderFrame(p, s, plan, 0); var sw = Stopwatch.StartNew(); const int n = 3; for (int i = 0; i < n; i++) VideoComposer.RenderFrame(p, s, plan, i);
            Console.WriteLine($"TIMING 1080p {cfg.Item1}: {sw.ElapsedMilliseconds / (double)n:F0} ms per frame (incl. frame load from memory)");
        }
        var s4 = One(PanelKind.Opd, ResolutionPreset.P1080); s4.Panels = new[] { PanelKind.Opd, PanelKind.FilteredOpd, PanelKind.Intensity, PanelKind.Surface3D }.Select(k => new PanelSpec(k)).ToList(); s4.StripPlot = true; s4.Fps = 10;
        var proc = Process.GetCurrentProcess(); proc.Refresh(); double before = proc.WorkingSet64 / 1048576.0; GC.Collect(); var sw2 = Stopwatch.StartNew(); RecSink? sink = null;
        var sum = VideoJob.Run(p, s4, (w, h) => sink = new RecSink(w, h) { Keep = false }); sw2.Stop(); proc.Refresh();
        Console.WriteLine($"TIMING 5-frame 1080p 4-panel video (OPD, filtered, intensity, 3-D 256 + strip plot) through the job incl. analysis pass: {sw2.Elapsed.TotalSeconds:F1} s ({sum.Frames} frames)");
        Console.WriteLine($"MEMORY working set before {before:F0} MB, peak of the test process {proc.PeakWorkingSet64 / 1048576.0:F0} MB (the process also holds the cached reconstructions of the fixture)");
        Assert.Equal(5, sink!.Count);
    }
}

static class VideoSettingsTestExt { public static void Blend(this VideoSettings s, int n) => s.BlendSteps = n; }
