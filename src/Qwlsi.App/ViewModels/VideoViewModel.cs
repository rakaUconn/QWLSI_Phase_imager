using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Qwlsi.Core;
using Qwlsi.Video;
using SV = Qwlsi.Video.StripValue;

namespace Qwlsi.App.ViewModels;

public sealed record Choice<T>(string Name, T Value) { public override string ToString() => Name; }

/// <summary>Frames of the video = processed rows of the Offline list (ordered / ranged by the Video tab); results come from the result cache (RAM -> disk -> recompute).</summary>
public sealed class AppFrameProvider : IFrameProvider
{
    readonly IReadOnlyList<FileItem> items; readonly OfflineViewModel offline;
    public AppFrameProvider(IReadOnlyList<FileItem> items, OfflineViewModel offline, QwlsiParams par) { this.items = items; this.offline = offline; Par = par; }
    public int Count => items.Count;
    public string NameOf(int i) => System.IO.Path.GetFileNameWithoutExtension(items[i].Path);
    public DateTime TimeOf(int i) { try { return File.GetLastWriteTime(items[i].Path); } catch { return DateTime.MinValue; } }
    public ReconOutcome Load(int i, CancellationToken ct) => offline.LoadResult(items[i], ct);
    public float[]? LoadRawDownsampled(int i) { try { return RawFrameLoader.LoadDownsampled(items[i].Path, Par.Dec); } catch (Exception e) { AppLog.Warn("raw frame not available: " + e.Message); return null; } }
    public QwlsiParams Par { get; }
}

/// <summary>Video tab: renders the processed frames of the Offline list (panels, overlays, strip plot, turntable) into MP4 / GIF / AVI / PNG sequence on a background thread.</summary>
public partial class VideoViewModel : ObservableObject
{
    readonly MainViewModel main; CancellationTokenSource? cts; ColorRangePlan? plan; string planKey = ""; int previewVersion;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed record PanelChoice(string Name, PanelKind? Kind) { public override string ToString() => Name; }
    public IReadOnlyList<PanelChoice> PanelChoices { get; } = new[]
    {
        new PanelChoice("OPD", PanelKind.Opd), new PanelChoice("Filtered OPD (Zernike selection)", PanelKind.FilteredOpd), new PanelChoice("Removed Zernike surface", PanelKind.RemovedZernike),
        new PanelChoice("Transmittance / intensity", PanelKind.Intensity), new PanelChoice("Wrapped field phase", PanelKind.WrappedPhase), new PanelChoice("Raw frame", PanelKind.RawFrame), new PanelChoice("3D surface", PanelKind.Surface3D),
    };
    public IReadOnlyList<PanelChoice> OptionalPanelChoices { get; }
    public IEnumerable<ColormapKind> Colormaps { get; } = Enum.GetValues<ColormapKind>();
    public IReadOnlyList<Choice<RangeMode>> RangeModes { get; } = new[] { new Choice<RangeMode>("Fixed range across all frames", RangeMode.FixedAcrossFrames), new Choice<RangeMode>("Auto per frame", RangeMode.AutoPerFrame), new Choice<RangeMode>("Percentile clipped (per frame)", RangeMode.PercentileClipped) };
    public IReadOnlyList<Choice<Units>> UnitChoices { get; } = new[] { new Choice<Units>("nm", Units.Nm), new Choice<Units>("µm", Units.Um), new Choice<Units>("waves", Units.Waves) };
    public IReadOnlyList<Choice<FrameOrder>> Orders { get; } = new[] { new Choice<FrameOrder>("by name", FrameOrder.ByName), new Choice<FrameOrder>("by time (file time)", FrameOrder.ByTime) };
    public IReadOnlyList<Choice<SV>> StripValues { get; } = new[] { new Choice<SV>("power [D]", SV.PowerD), new Choice<SV>("rms [nm]", SV.RmsNm), new Choice<SV>("PV [µm]", SV.PvUm), new Choice<SV>("astigmatism [nm]", SV.AstigNm), new Choice<SV>("Zernike term [nm]", SV.ZernikeTerm) };
    public IReadOnlyList<Choice<int>> StripTerms { get; } = ZernikeBasis.Terms(10).Select(t => new Choice<int>($"Z{t.J} {t.Name}", t.J)).ToArray();
    public IReadOnlyList<Choice<MeshResolution>> Meshes { get; } = new[] { new Choice<MeshResolution>("256 × 256 (fast)", MeshResolution.N256), new Choice<MeshResolution>("512 × 512", MeshResolution.N512), new Choice<MeshResolution>("full resolution", MeshResolution.Full) };
    public IReadOnlyList<Choice<ResolutionPreset>> Resolutions { get; } = new[] { new Choice<ResolutionPreset>("720p (1280 × 720)", ResolutionPreset.P720), new Choice<ResolutionPreset>("1080p (1920 × 1080)", ResolutionPreset.P1080), new Choice<ResolutionPreset>("native, cropped to the pupil", ResolutionPreset.NativeCropped) };
    public IReadOnlyList<Choice<VideoFormat>> Formats { get; } = new[] { new Choice<VideoFormat>("MP4 (H.264, ffmpeg)", VideoFormat.Mp4), new Choice<VideoFormat>("GIF", VideoFormat.Gif), new Choice<VideoFormat>("AVI (MJPEG)", VideoFormat.Avi), new Choice<VideoFormat>("PNG sequence", VideoFormat.PngSequence) };

    // frames
    [ObservableProperty] Choice<FrameOrder> order;
    [ObservableProperty] int firstFrame = 1;
    [ObservableProperty] int lastFrame;                      // 0 = to the end
    [ObservableProperty] string framesText = "";
    // panels
    [ObservableProperty] PanelChoice panel1;
    [ObservableProperty] PanelChoice? panel2;
    [ObservableProperty] PanelChoice? panel3;
    [ObservableProperty] PanelChoice? panel4;
    // colour
    [ObservableProperty] ColormapKind cmap = ColormapKind.Viridis;
    [ObservableProperty] Choice<RangeMode> range;
    [ObservableProperty] double pctLo = 1;
    [ObservableProperty] double pctHi = 99;
    [ObservableProperty] bool manualRange;
    [ObservableProperty] string manualMin = "", manualMax = "";
    [ObservableProperty] Choice<Units> unit;
    [ObservableProperty] bool colorbar = true;
    // overlays
    [ObservableProperty] bool overlayName = true;
    [ObservableProperty] bool overlayStats = true;
    [ObservableProperty] bool stripPlot;
    [ObservableProperty] Choice<SV> stripValue;
    [ObservableProperty] Choice<int> stripTerm;
    // 3D / turntable
    [ObservableProperty] Choice<MeshResolution> mesh;
    [ObservableProperty] bool exaggerationAuto = true;
    [ObservableProperty] string exaggeration = "1000";
    [ObservableProperty] double elevation = 30;
    [ObservableProperty] double azimuth = -45;
    [ObservableProperty] bool axes = true;
    [ObservableProperty] bool surfaceFiltered;
    [ObservableProperty] bool turntable;
    [ObservableProperty] double turntableSeconds = 10;
    [ObservableProperty] int turntableFrame = 1;
    // output
    [ObservableProperty] int fps = 10;
    [ObservableProperty] Choice<ResolutionPreset> resolution;
    [ObservableProperty] Choice<VideoFormat> format;
    [ObservableProperty] int blendSteps;
    [ObservableProperty] string outputPath = "";
    [ObservableProperty] string estimate = "";
    [ObservableProperty] string ffmpegStatus = "";
    // run
    [ObservableProperty] bool isRendering;
    [ObservableProperty] double progress;
    [ObservableProperty] string progressText = "";
    [ObservableProperty] string message = "Process frames on the Offline tab, choose panels and press Render. The Preview shows exactly what the video will contain.";
    [ObservableProperty] BitmapSource? previewImage;
    [ObservableProperty] int previewIndex = 1;
    [ObservableProperty] int previewMax = 1;
    [ObservableProperty] string previewInfo = "";

    public double LastRenderSeconds { get; private set; } public double LastPeakMb { get; private set; } public string LastOutputPath { get; private set; } = "";
    public OfflineViewModel Offline => main.Offline;

    static readonly HashSet<string> Passive = new() { nameof(Estimate), nameof(Progress), nameof(ProgressText), nameof(Message), nameof(PreviewImage), nameof(PreviewIndex), nameof(PreviewMax), nameof(PreviewInfo), nameof(IsRendering), nameof(FfmpegStatus), nameof(FramesText), nameof(OutputPath) };

    public VideoViewModel(MainViewModel m)
    {
        main = m; OptionalPanelChoices = new PanelChoice[] { new PanelChoice("(none)", null) }.Concat(PanelChoices).ToArray();
        order = Orders[0]; panel1 = PanelChoices[0]; panel2 = OptionalPanelChoices[0]; panel3 = OptionalPanelChoices[0]; panel4 = OptionalPanelChoices[0]; range = RangeModes[0]; unit = UnitChoices[0]; stripValue = StripValues[0]; stripTerm = StripTerms[4];
        mesh = Meshes[0]; resolution = Resolutions[1]; format = Formats[0]; cmap = Enum.TryParse<ColormapKind>(m.Settings.Colormap, out var ck) ? ck : ColormapKind.Viridis;
        m.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.SelectedTab) && m.SelectedTab == 3) RefreshFrames(); };
        m.Offline.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(OfflineViewModel.SelectedFile) && Turntable) { } };
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != null && !Passive.Contains(e.PropertyName)) { plan = null; UpdateEstimate(); }
    }

    // ------------------------------------------------------------------------------------------------ frames / settings
    /// <summary>Processed rows, ordered (name or file time) and cut to [First..Last].</summary>
    public List<FileItem> BuildFrames()
    {
        var all = main.Offline.ProcessedFiles(); var ord = FrameList.Order(all, f => f.Name, f => { try { return File.GetLastWriteTime(f.Path); } catch { return DateTime.MinValue; } }, Order.Value);
        return FrameList.Range(ord, FirstFrame, LastFrame);
    }

    [RelayCommand]
    public void RefreshFrames()
    {
        int total = main.Offline.ProcessedFiles().Count; var f = BuildFrames();
        FramesText = total == 0 ? "No processed frames: process frames on the Offline tab first." : $"{f.Count} of {total} processed frame(s) {(Turntable ? "(turntable uses one of them)" : "")}";
        PreviewMax = Math.Max(1, Turntable ? VideoSettings.TurntableFrames(TurntableSeconds, Fps) : f.Count); if (PreviewIndex > PreviewMax) PreviewIndex = PreviewMax;
        UpdateFfmpegStatus(); UpdateEstimate();
    }

    public VideoSettings BuildSettings(ZernikeCircle? circle)
    {
        var panels = new List<PanelSpec> { new PanelSpec(Panel1.Kind!.Value) };
        foreach (var p in new[] { Panel2, Panel3, Panel4 }) if (p?.Kind != null) panels.Add(new PanelSpec(p.Kind.Value));
        var zo = main.ZernikeVm.Orientation;
        var s = new VideoSettings
        {
            Panels = panels, Cmap = Cmap, Range = Range.Value, PctLo = PctLo, PctHi = PctHi, Unit = Unit.Value, Colorbar = Colorbar, OverlayName = OverlayName, OverlayStats = OverlayStats, StripPlot = StripPlot, Strip = StripValue.Value, StripTerm = StripTerm.Value,
            Zernike = new VideoZernikeSpec { Selection = main.ZernikeVm.Selection(), Orientation = new ZernikeOrientation { FlipX = zo.FlipX, FlipY = zo.FlipY }, Circle = circle },
            Surface = new SurfaceSettings { Mesh = Mesh.Value, Exaggeration = ExaggerationAuto ? null : (double.TryParse(Exaggeration, NumberStyles.Float, Inv, out var ex) && ex > 0 ? ex : 1000), ElevationDeg = Elevation, AzimuthDeg = Azimuth, Axes = Axes, Filtered = SurfaceFiltered },
            Turntable = Turntable, TurntableSeconds = Math.Max(1, TurntableSeconds), TurntableFrame = Math.Max(0, TurntableFrame - 1), Fps = Math.Clamp(Fps, 1, 60), Resolution = Resolution.Value, BlendSteps = Math.Clamp(BlendSteps, 0, 20),
        };
        if (ManualRange && double.TryParse(ManualMin, NumberStyles.Float, Inv, out var a) && double.TryParse(ManualMax, NumberStyles.Float, Inv, out var b) && b > a) { s.ManualMin = a; s.ManualMax = b; }
        return s;
    }

    void UpdateEstimate()
    {
        try
        {
            if (Panel1?.Kind == null) { Estimate = ""; return; } int n = main.Offline.ProcessedFiles().Count; if (n == 0) { Estimate = ""; return; }
            n = Math.Max(1, BuildFrames().Count); Estimate = VideoEstimate.Quick(BuildSettings(null), n, Format.Value, EncodeOptions(), plan).Text;
        }
        catch (Exception e) { Estimate = e.Message; }
    }

    VideoEncodeOptions EncodeOptions() => new() { FfmpegPath = main.Settings.FfmpegPath };

    void UpdateFfmpegStatus()
    {
        string? p = FfmpegLocator.Find(main.Settings.FfmpegPath);
        if (p == null) { FfmpegStatus = "ffmpeg.exe not found: MP4 is saved as AVI (MJPEG) instead; GIF, AVI and PNG sequence work without it."; return; }
        FfmpegStatus = "ffmpeg: " + p; _ = Task.Run(() => { var v = FfmpegLocator.VersionString(p); var enc = FfmpegLocator.ProbeH264Encoder(p); return (v, enc); }).ContinueWith(t =>
        { if (t.IsCompletedSuccessfully) System.Windows.Application.Current?.Dispatcher.Invoke(() => FfmpegStatus = $"ffmpeg: {p}\n{t.Result.v}\nH.264 encoder: {t.Result.enc ?? "none works on this PC → MP4 falls back to AVI"}"); });
    }

    [RelayCommand]
    void BrowseFfmpeg()
    {
        var dlg = new OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe|executables|*.exe", Title = "Choose ffmpeg.exe (an LGPL build is bundled in the 'ffmpeg' folder of the program)" }; if (dlg.ShowDialog() != true) return;
        main.Settings.FfmpegPath = dlg.FileName; main.Settings.Save(); UpdateFfmpegStatus();
    }

    [RelayCommand]
    void BrowseOutput()
    {
        if (Format.Value == VideoFormat.PngSequence) { var d = new OpenFolderDialog { Title = "Folder for the PNG sequence" }; if (d.ShowDialog() == true) OutputPath = d.FolderName; return; }
        string ext = VideoSinkFactory.Extension(Format.Value); var dlg = new SaveFileDialog { Filter = $"{Format.Name}|*{ext}", FileName = DefaultName() + ext, InitialDirectory = Directory.Exists(main.Offline.ExportFolderPath()) ? main.Offline.ExportFolderPath() : null };
        if (dlg.ShowDialog() == true) OutputPath = dlg.FileName;
    }

    string DefaultName() => "video_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv);
    string ResolveOutput()
    {
        string p = OutputPath.Trim(); string ext = VideoSinkFactory.Extension(Format.Value);
        if (Format.Value == VideoFormat.PngSequence) return string.IsNullOrEmpty(p) ? System.IO.Path.Combine(main.Offline.ExportFolderPath(), DefaultName() + "_frames") : p;
        if (string.IsNullOrEmpty(p)) return System.IO.Path.Combine(main.Offline.ExportFolderPath(), DefaultName() + ext);
        return System.IO.Path.GetExtension(p).Length == 0 || Directory.Exists(p) ? System.IO.Path.Combine(p, DefaultName() + ext) : p;
    }

    // ------------------------------------------------------------------------------------------------ plan / preview
    async Task<(AppFrameProvider prov, VideoSettings s, ColorRangePlan plan)?> PrepareAsync(CancellationToken ct, bool needPlan = true)
    {
        if (Panel1?.Kind == null) { Message = "Choose at least the first panel."; return null; }
        var frames = BuildFrames(); if (frames.Count == 0) { Message = "No processed frames in the chosen range: process frames on the Offline tab first."; return null; }
        var par = main.Settings.Params.Clone(); ZernikeCircle? circle = null;
        if (main.ZernikeAll.CommonCircle && (Panel1.Kind is PanelKind.FilteredOpd or PanelKind.RemovedZernike || new[] { Panel2, Panel3, Panel4 }.Any(p => p?.Kind is PanelKind.FilteredOpd or PanelKind.RemovedZernike) || SurfaceFiltered || StripValue.Value == SV.ZernikeTerm))
        {
            try { circle = await Task.Run(() => main.ZernikeAll.GetPupilCircle(frames, ct), ct); } catch (InvalidOperationException e) { Message = "Common circle: " + e.Message; return null; }
        }
        var s = BuildSettings(circle);
        if (s.Turntable && !s.Panels.Any(p => p.Kind == PanelKind.Surface3D)) { Message = "The turntable needs a '3D surface' panel: choose it for one of the panels."; return null; }
        if (s.Turntable && s.TurntableFrame >= frames.Count) { s.TurntableFrame = frames.Count - 1; TurntableFrame = frames.Count; }
        var prov = new AppFrameProvider(frames, main.Offline, par);
        string key = JsonSerializer.Serialize(new { s.Panels, s.Cmap, s.Range, s.PctLo, s.PctHi, s.ManualMin, s.ManualMax, s.Unit, s.StripPlot, s.Strip, s.StripTerm, s.Zernike, Mesh = s.Surface.Mesh, s.Surface.Exaggeration, s.Surface.Filtered, s.Turntable, s.TurntableFrame, s.Resolution, s.OverlayName, s.OverlayStats, s.Colorbar }) + "|" + string.Join("|", frames.Select(f => f.Path)) + par.Lambda;
        if (!needPlan) return (prov, s, plan!);
        if (plan == null || key != planKey)
        {
            ProgressText = "Analysing frames (colour ranges)…"; var pr = new Progress<VideoProgress>(vp => { Progress = vp.Total == 0 ? 0 : vp.Done / (double)vp.Total; ProgressText = $"{vp.Phase}: {vp.Done}/{vp.Total} {vp.Message}"; });
            plan = await Task.Run(() => ColorRangePlan.Build(prov, s, pr, ct), ct); planKey = key; ProgressText = "";
        }
        return (prov, s, plan);
    }

    [RelayCommand]
    public async Task Preview()
    {
        if (IsRendering) return; int v = ++previewVersion; IsRendering = true; main.IsBusy = true; cts = new CancellationTokenSource(); var ct = cts.Token;
        try
        {
            var prep = await PrepareAsync(ct); if (prep == null) return; var (prov, s, pl) = prep.Value;
            int count = s.Turntable ? VideoSettings.TurntableFrames(s.TurntableSeconds, s.Fps) : prov.Count; PreviewMax = Math.Max(1, count); int idx = Math.Clamp(PreviewIndex, 1, PreviewMax) - 1;
            var sw = Stopwatch.StartNew(); var rf = await Task.Run(() => VideoComposer.RenderPreview(prov, s, pl, idx, ct), ct); if (v != previewVersion) return;
            var bmp = BitmapSource.Create(rf.Width, rf.Height, 96, 96, PixelFormats.Bgra32, null, rf.Bgra, rf.Width * 4); bmp.Freeze(); PreviewImage = bmp;
            PreviewInfo = $"{(s.Turntable ? $"azimuth {rf.AzimuthDeg:0}°" : prov.NameOf(idx))} — {rf.Width}×{rf.Height}, rendered in {sw.ElapsedMilliseconds} ms";
            var est = await Task.Run(() => VideoEstimate.Measured(prov, s, pl, Format.Value, EncodeOptions(), ct), ct); Estimate = est.Text; Message = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Message = "Preview failed: " + e.Message; AppLog.Error("video preview failed", e); }
        finally { IsRendering = false; main.IsBusy = false; Progress = 0; ProgressText = ""; }
    }

    partial void OnPreviewIndexChanged(int value) { if (!IsRendering && PreviewImage != null && plan != null) _ = Preview(); }

    // ------------------------------------------------------------------------------------------------ render
    [RelayCommand]
    public async Task Render()
    {
        if (IsRendering) return; if (main.IsBusy) { Message = "Another job is running: wait for it to finish."; return; }
        IsRendering = true; main.IsBusy = true; cts = new CancellationTokenSource(); var ct = cts.Token; Progress = 0; Message = "";
        var sw = Stopwatch.StartNew(); using var mw = new MemoryWatch();      // whole job: analysis pass + rendering + encoding
        try
        {
            var prep = await PrepareAsync(ct); if (prep == null) return; var (prov, s, pl) = prep.Value;
            var fmt = Format.Value; string path = ResolveOutput(); Directory.CreateDirectory(fmt == VideoFormat.PngSequence ? path : System.IO.Path.GetDirectoryName(path)!);
            string? note = null; string finalPath = path; VideoFormat finalFmt = fmt; var opts = EncodeOptions();
            var prog = new Progress<VideoProgress>(vp => { Progress = vp.Total == 0 ? 0 : vp.Done / (double)vp.Total; ProgressText = $"{vp.Phase}: {vp.Done}/{vp.Total}  {vp.Message}  — elapsed {vp.Elapsed:mm\\:ss}, about {vp.Eta:mm\\:ss} left"; });
            AppLog.Info($"video render: {prov.Count} frame(s), {fmt}, {s.Fps} fps, {s.Resolution}, panels {string.Join("+", s.Panels.Select(p => p.Kind))} -> {path}");
            var sum = await VideoJob.RunAsync(prov, s, (w, h) => { var r = VideoSinkFactory.Create(fmt, path, w, h, s.FpsClamped, opts); finalPath = r.Path; note = r.FallbackNote; finalFmt = r.Format; return r.Sink; }, prog, ct, pl);
            LastRenderSeconds = sw.Elapsed.TotalSeconds; LastPeakMb = mw.PeakWorkingSetMb; LastOutputPath = finalPath;
            long size = 0; try { size = finalFmt == VideoFormat.PngSequence ? Directory.GetFiles(finalPath, "*.png").Sum(f => new FileInfo(f).Length) : new FileInfo(finalPath).Length; } catch { }
            Message = $"Saved {finalPath}\n{sum.Frames} frames, {sum.Width}×{sum.Height}, {VideoEstimate.FormatSize(size)}, {s.Fps} fps = {sum.Frames / (double)s.FpsClamped:0.0#} s of video, rendered in {LastRenderSeconds:F1} s including the analysis pass ({sum.SinkDescription}); peak working set of the program during the render {LastPeakMb:F0} MB." + (note != null ? "\n" + note : "");
            main.StatusMessage = "Video saved: " + finalPath; AppLog.Info("video render done: " + Message.Replace("\n", " | "));
        }
        catch (OperationCanceledException) { Message = "Cancelled — no file was kept."; main.StatusMessage = "Video cancelled."; }
        catch (Exception e) { Message = "Rendering failed: " + e.Message; AppLog.Error("video render failed", e); }
        finally { IsRendering = false; main.IsBusy = false; Progress = 0; ProgressText = ""; Processor.TrimProcessMemory(); }
    }

    [RelayCommand] void Cancel() { cts?.Cancel(); ProgressText = "Cancelling…"; }

    [RelayCommand]
    void OpenOutput()
    {
        string p = string.IsNullOrEmpty(LastOutputPath) ? main.Offline.ExportFolderPath() : LastOutputPath;
        if (File.Exists(p)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true });
        else { Directory.CreateDirectory(p); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p}\"") { UseShellExecute = true }); }
    }
}
