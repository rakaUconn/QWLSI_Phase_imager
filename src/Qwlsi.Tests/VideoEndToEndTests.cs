using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using Qwlsi.Core;
using Qwlsi.Video;

namespace Qwlsi.Tests;

/// <summary>1.1.0 end to end: REAL frames -> VideoJob -> REAL encoders (MP4 via the bundled ffmpeg, GIF, AVI, PNG sequence); the frame count and size are checked by decoding the files independently.</summary>
[Collection("heavy")]
public class VideoEndToEndTests
{
    readonly GoldenFixture fx;
    public VideoEndToEndTests(GoldenFixture f) { fx = f; }
    static readonly string[] Names = { "concave_1_img", "concave_2_img", "concave_1-5_img", "convex_1_img", "convex_2_img" };

    sealed class Provider : IFrameProvider
    {
        readonly List<ReconOutcome> o; public QwlsiParams Par { get; }
        public Provider(List<ReconOutcome> o, QwlsiParams par) { this.o = o; Par = par; }
        public int Count => o.Count; public string NameOf(int i) => Path.GetFileName(o[i].File); public DateTime TimeOf(int i) => DateTime.UnixEpoch.AddMinutes(i);
        public ReconOutcome Load(int i, CancellationToken ct) => o[i]; public float[]? LoadRawDownsampled(int i) => null;
    }

    Provider Frames() => new(Names.Select(n => new ReconOutcome { File = Path.Combine(fx.Root, n + ".tif"), Mode = "without reference", Result = fx.Recon(n), CarriersUsed = fx.Calib }).ToList(), fx.Par);

    static string? Ffmpeg()
    {
        var f = FfmpegLocator.Find(); if (f != null) return f;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent) { var c = Path.Combine(d.FullName, "third_party", "ffmpeg", "ffmpeg.exe"); if (File.Exists(c)) return c; }
        return null;
    }


    static (int frames, string size) FfmpegDecode(string ff, string file)
    {
        var (_, so, se) = FfmpegLocator.Run(ff, $"-hide_banner -v info -i \"{file}\" -f null -", 60000); string t = so + se;
        var fr = Regex.Matches(t, @"frame=\s*(\d+)"); var sz = Regex.Match(t, @"Video: h264[^\n]*?, (\d+x\d+)");
        return (fr.Count > 0 ? int.Parse(fr[^1].Groups[1].Value) : -1, sz.Success ? sz.Groups[1].Value : "?");
    }

    [Fact]
    public void Five_real_frames_render_to_mp4_gif_avi_png_with_correct_frame_count_and_size_and_report_time_and_memory()
    {
        var prov = Frames(); string dir = Path.Combine(Path.GetTempPath(), "qwlsi_e2e_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); var ff = Ffmpeg();
        try
        {
            var s = new VideoSettings { Panels = new() { new PanelSpec(PanelKind.Opd), new PanelSpec(PanelKind.FilteredOpd), new PanelSpec(PanelKind.Intensity), new PanelSpec(PanelKind.Surface3D) }, Fps = 10, Resolution = ResolutionPreset.P720, StripPlot = true };
            foreach (var fmt in new[] { VideoFormat.Mp4, VideoFormat.Gif, VideoFormat.Avi, VideoFormat.PngSequence })
            {
                if (fmt == VideoFormat.Mp4 && ff == null) { Console.WriteLine("SKIP MP4: ffmpeg.exe not found (QWLSI_FFMPEG / third_party\\ffmpeg)"); continue; }
                string path = Path.Combine(dir, "v_" + fmt + VideoSinkFactory.Extension(fmt)); var opt = new VideoEncodeOptions { FfmpegPath = ff }; SinkResult? sr = null; var sw = Stopwatch.StartNew(); using var mw = new MemoryWatch();
                var sum = VideoJob.Run(prov, s, (w, h) => (sr = VideoSinkFactory.Create(fmt, path, w, h, s.Fps, opt)).Sink); sw.Stop();
                Assert.Equal(fmt, sr!.Format); Assert.Equal(5, sum.Frames); Assert.Equal((1280, 720), (sum.Width, sum.Height));
                long bytes = fmt == VideoFormat.PngSequence ? Directory.GetFiles(sr.Path, "*.png").Sum(f => new FileInfo(f).Length) : new FileInfo(sr.Path).Length; Assert.True(bytes > 1000);
                Console.WriteLine($"E2E {fmt}: 5 frames 1280x720 4 panels + strip plot in {sw.Elapsed.TotalSeconds:F2} s, {bytes / 1024} kB, peak working set {mw.PeakWorkingSetMb:F0} MB ({sum.SinkDescription})");
                switch (fmt)
                {
                    case VideoFormat.Mp4: { var (n, size) = FfmpegDecode(ff!, sr.Path); Assert.Equal(5, n); Assert.Equal("1280x720", size); break; }
                    case VideoFormat.Gif: { using var img = Image.FromFile(sr.Path); Assert.Equal((1280, 720), (img.Width, img.Height)); Assert.Equal(5, img.GetFrameCount(FrameDimension.Time)); break; }
                    case VideoFormat.Avi: { var b = File.ReadAllBytes(sr.Path); Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(b, 0, 4)); int avih = Array.IndexOf(b, (byte)'a', 12); string txt = System.Text.Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 400)); int p = txt.IndexOf("avih", StringComparison.Ordinal); Assert.True(p > 0);
                        Assert.Equal(5, BitConverter.ToInt32(b, p + 8 + 16)); Assert.Equal(1280, BitConverter.ToInt32(b, p + 8 + 32)); Assert.Equal(720, BitConverter.ToInt32(b, p + 8 + 36)); break; }
                    case VideoFormat.PngSequence: { var files = Directory.GetFiles(sr.Path, "*.png").OrderBy(x => x).ToArray(); Assert.Equal(5, files.Length); using var im = Image.FromFile(files[0]); Assert.Equal((1280, 720), (im.Width, im.Height)); break; }
                }
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Blend_and_turntable_frame_counts_and_mp4_without_ffmpeg_falls_back_to_avi()
    {
        var prov = Frames(); string dir = Path.Combine(Path.GetTempPath(), "qwlsi_e2e2_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var blend = new VideoSettings { Panels = new() { new PanelSpec(PanelKind.Opd) }, Fps = 20, Resolution = ResolutionPreset.P720, BlendSteps = 2 }; int n = 0;
            var s1 = VideoJob.Run(prov, blend, (w, h) => new CountSink(w, h, () => n++)); Assert.Equal(5 + 4 * 2, s1.Frames); Assert.Equal(13, n);          // blending is opt-in: 0 = none
            n = 0; var plain = new VideoSettings { Panels = new() { new PanelSpec(PanelKind.Opd) }, Fps = 20, Resolution = ResolutionPreset.P720 }; Assert.Equal(5, VideoJob.Run(prov, plain, (w, h) => new CountSink(w, h, () => n++)).Frames);
            n = 0; var tt = new VideoSettings { Panels = new() { new PanelSpec(PanelKind.Surface3D) }, Fps = 10, Resolution = ResolutionPreset.P720, Turntable = true, TurntableSeconds = 2, TurntableFrame = 1 };
            Assert.Equal(20, VideoJob.Run(prov, tt, (w, h) => new CountSink(w, h, () => n++)).Frames);
            var path = Path.Combine(dir, "x.mp4"); SinkResult? sr = null; VideoJob.Run(prov, plain, (w, h) => (sr = VideoSinkFactory.Create(VideoFormat.Mp4, path, w, h, 20, new VideoEncodeOptions { DisableFfmpeg = true })).Sink);
            Assert.Equal(VideoFormat.Avi, sr!.Format); Assert.NotNull(sr.FallbackNote); Assert.EndsWith(".avi", sr.Path); Assert.True(File.Exists(sr.Path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    sealed class CountSink : IVideoSink
    {
        readonly Action tick; public CountSink(int w, int h, Action tick) { Width = w; Height = h; this.tick = tick; }
        public int Width { get; } public int Height { get; } public string Description => "count"; public void WriteFrame(ReadOnlySpan<byte> bgra) { tick(); } public void Complete() { } public void Dispose() { }
    }
}
