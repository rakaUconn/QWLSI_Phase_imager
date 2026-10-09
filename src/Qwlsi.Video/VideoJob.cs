using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;

namespace Qwlsi.Video;

public sealed record VideoSummary(int Frames, int Width, int Height, double Seconds, string SinkDescription);

/// <summary>
/// Background render of a video: analysis pass (colour ranges, crop, exaggeration, strip series; skipped when a plan is given), then frame by frame -> sink. One source frame in memory at a time;
/// cancellation stops between frames and disposes the sink WITHOUT Complete (partial output removed); a failing frame raises <see cref="VideoRenderException"/> naming the frame.
/// </summary>
public static class VideoJob
{
    public static Task<VideoSummary> RunAsync(IFrameProvider p, VideoSettings s, Func<int, int, IVideoSink> sinkFactory, IProgress<VideoProgress>? progress = null, CancellationToken ct = default, ColorRangePlan? plan = null)
        => Task.Run(() => Run(p, s, sinkFactory, progress, ct, plan));

    public static VideoSummary Run(IFrameProvider p, VideoSettings s, Func<int, int, IVideoSink> sinkFactory, IProgress<VideoProgress>? progress = null, CancellationToken ct = default, ColorRangePlan? plan = null)
    {
        if (p.Count <= 0) throw new VideoRenderException("There are no processed frames to render.");
        var sw = Stopwatch.StartNew(); plan ??= ColorRangePlan.Build(p, s, progress, ct); ct.ThrowIfCancellationRequested();
        var L = VideoComposer.Layout(s, plan); int total = s.OutputFrames(p.Count), written = 0; int B = s.Turntable ? 0 : Math.Max(0, s.BlendSteps);
        TimeSpan Eta() => written == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds / written * (total - written));
        void Report(string msg) => progress?.Report(new VideoProgress("Rendering", written, total, sw.Elapsed, Eta(), msg));
        var sink = sinkFactory(L.Width, L.Height); string desc = sink.Description;
        try
        {
            if (s.Turntable)
            {
                var fc = FrameContext.Create(p, Math.Clamp(s.TurntableFrame, 0, p.Count - 1), s, ct);
                for (int k = 0; k < total; k++) { ct.ThrowIfCancellationRequested(); var rf = VideoComposer.Render(fc, s, plan, 1, 1, s.TurntableAzimuth(k, total)); sink.WriteFrame(rf.Bgra); written++; Report($"azimuth {rf.AzimuthDeg:0}°"); }
            }
            else
            {
                byte[]? prev = null, tmp = B > 0 ? new byte[L.Width * L.Height * 4] : null;
                for (int k = 0; k < p.Count; k++)
                {
                    ct.ThrowIfCancellationRequested(); var fc = FrameContext.Create(p, k, s, ct); var rf = VideoComposer.Render(fc, s, plan, k + 1, p.Count, s.Surface.AzimuthDeg);
                    if (prev != null) for (int j = 1; j <= B; j++) { ct.ThrowIfCancellationRequested(); sink.WriteFrame(VideoComposer.Blend(prev, rf.Bgra, j / (B + 1.0), tmp)); written++; }
                    sink.WriteFrame(rf.Bgra); written++; prev = rf.Bgra; Report(fc.Name);
                }
            }
            ct.ThrowIfCancellationRequested(); sink.Complete();
        }
        finally { sink.Dispose(); }
        return new VideoSummary(written, L.Width, L.Height, sw.Elapsed.TotalSeconds, desc);
    }
}

public sealed record VideoEstimateResult(int Width, int Height, int Frames, double Seconds, long Bytes, bool Measured, string Text);

/// <summary>Duration and file-size estimate shown before rendering. MP4: bitrate-based (BitsPerPixel * w * h * frames / 8). Other formats: measured on the first rendered frame when a provider is given (PNG / JPEG size per frame), rough per-pixel constants otherwise.</summary>
public static class VideoEstimate
{
    public static string FormatSize(long b) => b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB" : b >= 1L << 20 ? (b / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : Math.Max(1, b / 1024).ToString(CultureInfo.InvariantCulture) + " kB";

    public static VideoEstimateResult Quick(VideoSettings s, int sourceFrames, VideoFormat fmt, VideoEncodeOptions? o = null, ColorRangePlan? plan = null)
    {
        o ??= new VideoEncodeOptions(); int w, h;
        if (s.Resolution == ResolutionPreset.NativeCropped && plan != null) { var L = VideoComposer.Layout(s, plan); w = L.Width; h = L.Height; }
        else if (s.Resolution == ResolutionPreset.P720) { w = 1280; h = 720; } else if (s.Resolution == ResolutionPreset.P1080) { w = 1920; h = 1080; } else { w = 1000; h = 1000; }
        int frames = s.OutputFrames(sourceFrames); long bytes = fmt switch { VideoFormat.Mp4 => (long)(o.BitsPerPixel * w * h * frames / 8), VideoFormat.Avi => (long)(0.25 * w * h * frames), VideoFormat.Gif => (long)(0.7 * w * h * frames), _ => (long)(0.8 * w * h * frames) };
        return Make(s, w, h, frames, bytes, false, fmt);
    }

    public static VideoEstimateResult Measured(IFrameProvider p, VideoSettings s, ColorRangePlan plan, VideoFormat fmt, VideoEncodeOptions? o = null, CancellationToken ct = default)
    {
        o ??= new VideoEncodeOptions(); var L = VideoComposer.Layout(s, plan); int frames = s.OutputFrames(p.Count);
        if (fmt == VideoFormat.Mp4) return Make(s, L.Width, L.Height, frames, (long)(o.BitsPerPixel * L.Width * L.Height * frames / 8), false, fmt);
        var rf = VideoComposer.RenderFrame(p, s, plan, 0, ct); long one = FrameBytes(rf, fmt, o);
        return Make(s, L.Width, L.Height, frames, one * frames + (fmt == VideoFormat.Avi ? 64L * frames : 0), true, fmt);
    }

    static long FrameBytes(RenderedFrame rf, VideoFormat fmt, VideoEncodeOptions o)
    {
        using var bmp = new Bitmap(rf.Width, rf.Height, PixelFormat.Format32bppArgb); var d = bmp.LockBits(new Rectangle(0, 0, rf.Width, rf.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < rf.Height; y++) System.Runtime.InteropServices.Marshal.Copy(rf.Bgra, y * rf.Width * 4, d.Scan0 + y * d.Stride, rf.Width * 4); } finally { bmp.UnlockBits(d); }
        using var ms = new MemoryStream();
        if (fmt == VideoFormat.Avi)
        {
            var enc = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid); using var ps = new EncoderParameters(1); ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(o.JpegQuality, 1, 100)); bmp.Save(ms, enc, ps); return ms.Length;
        }
        bmp.Save(ms, ImageFormat.Png); return fmt == VideoFormat.Gif ? (long)(ms.Length * 1.2) : ms.Length;      // GIF: rough (palette LZW is within ~20 % of PNG on smooth maps)
    }

    static VideoEstimateResult Make(VideoSettings s, int w, int h, int frames, long bytes, bool measured, VideoFormat fmt)
    {
        double sec = frames / (double)Math.Clamp(s.Fps, 1, 60);
        return new VideoEstimateResult(w, h, frames, sec, bytes, measured, $"{frames} frames, {sec.ToString("0.0#", CultureInfo.InvariantCulture)} s at {Math.Clamp(s.Fps, 1, 60)} fps, {w}x{h}, about {FormatSize(bytes)}{(fmt == VideoFormat.PngSequence ? " (PNG sequence)" : "")}");
    }
}
