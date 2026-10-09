using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.RegularExpressions;
using Qwlsi.Video;

namespace Qwlsi.Tests;

/// <summary>Encoders of the Video tab: native PNG sequence / GIF / AVI (MJPEG) and MP4 through ffmpeg.exe. Synthetic frames, no real data.</summary>
public class VideoSinkTests
{
    const int W = 320, H = 240, N = 5, Fps = 10;
    static string Tmp() { var d = Path.Combine(Path.GetTempPath(), "qwlsi_vs_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    /// <summary>Moving gradient: blue follows x, green follows y, red encodes the frame number; a bright block moves 20 px per frame.</summary>
    static byte[] Frame(int i, int w = W, int h = H)
    {
        var b = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4; bool block = x >= 20 * i && x < 20 * i + w / 8 && y >= h / 3 && y < h / 3 + h / 6;
            b[o] = (byte)(block ? 255 : x * 255 / (w - 1)); b[o + 1] = (byte)(block ? 255 : y * 255 / (h - 1)); b[o + 2] = (byte)(block ? 255 : 40 + 40 * i); b[o + 3] = 255;
        }
        return b;
    }

    static string? Ffmpeg()
    {
        var f = FfmpegLocator.Find(); if (f != null) return f;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent) { var c = Path.Combine(d.FullName, "third_party", "ffmpeg", "ffmpeg.exe"); if (File.Exists(c)) return c; }
        return null;
    }

    static (double meanAbs, int w, int h) Compare(Bitmap bmp, byte[] bgra, int w, int h)
    {
        long sum = 0; int n = 0;
        for (int y = 0; y < h; y += 3) for (int x = 0; x < w; x += 3) { var c = bmp.GetPixel(x, y); int o = (y * w + x) * 4; sum += Math.Abs(c.B - bgra[o]) + Math.Abs(c.G - bgra[o + 1]) + Math.Abs(c.R - bgra[o + 2]); n += 3; }
        return ((double)sum / n, bmp.Width, bmp.Height);
    }

    static SinkResult Make(VideoFormat f, string path, int w = W, int h = H, int fps = Fps, VideoEncodeOptions? o = null) => VideoSinkFactory.Create(f, path, w, h, fps, o);

    // ------------------------------------------------------------------------------------------------ PNG sequence
    [Fact]
    public void Png_sequence_has_one_lossless_png_per_frame()
    {
        var dir = Tmp(); try
        {
            var r = Make(VideoFormat.PngSequence, Path.Combine(dir, "png")); Assert.Equal(VideoFormat.PngSequence, r.Format); Assert.Null(r.FallbackNote);
            using (var s = r.Sink) { for (int i = 0; i < N; i++) s.WriteFrame(Frame(i)); s.Complete(); }
            var files = Directory.GetFiles(r.Path, "*.png").OrderBy(x => x).ToArray(); Assert.Equal(N, files.Length); Assert.Equal("frame_000001.png", Path.GetFileName(files[0])); Assert.Equal("frame_000005.png", Path.GetFileName(files[4]));
            for (int i = 0; i < N; i++)
            {
                var hd = File.ReadAllBytes(files[i]); Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, hd[..4]);
                Assert.Equal(W, hd[16] << 24 | hd[17] << 16 | hd[18] << 8 | hd[19]); Assert.Equal(H, hd[20] << 24 | hd[21] << 16 | hd[22] << 8 | hd[23]);
                using var bmp = new Bitmap(files[i]); var (m, w, h) = Compare(bmp, Frame(i), W, H); Assert.Equal((W, H), (w, h)); Assert.Equal(0.0, m);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------------------------------------ GIF
    [Fact]
    public void Gif_has_five_frames_loop_extension_delays_and_decodes()
    {
        var dir = Tmp(); try
        {
            var r = Make(VideoFormat.Gif, Path.Combine(dir, "a.gif")); Assert.Equal(".gif", Path.GetExtension(r.Path));
            using (var s = r.Sink) { for (int i = 0; i < N; i++) s.WriteFrame(Frame(i)); s.Complete(); }
            var b = File.ReadAllBytes(r.Path); Assert.Equal("GIF89a", Encoding.ASCII.GetString(b, 0, 6)); Assert.Equal(W, b[6] | b[7] << 8); Assert.Equal(H, b[8] | b[9] << 8); Assert.Equal(0x3B, b[^1]);
            int p = 13, images = 0; bool loop = false; var delays = new List<int>();
            while (b[p] != 0x3B)
            {
                if (b[p] == 0x21 && b[p + 1] == 0xFF) { loop = Encoding.ASCII.GetString(b, p + 3, 11) == "NETSCAPE2.0"; p += 3 + 11; while (b[p] != 0) p += b[p] + 1; p++; }
                else if (b[p] == 0x21 && b[p + 1] == 0xF9) { delays.Add(b[p + 4] | b[p + 5] << 8); p += 8; }
                else if (b[p] == 0x2C) { images++; Assert.Equal(W, b[p + 5] | b[p + 6] << 8); Assert.Equal(H, b[p + 7] | b[p + 8] << 8); bool lct = (b[p + 9] & 0x80) != 0; p += 10; if (lct) p += 3 << ((b[p - 1] & 7) + 1); p++; while (b[p] != 0) p += b[p] + 1; p++; }
                else Assert.Fail("unexpected block " + b[p] + " at " + p);
            }
            Assert.Equal(N, images); Assert.True(loop); Assert.Equal(N, delays.Count); Assert.All(delays, d => Assert.Equal(10, d));
            using var img = Image.FromFile(r.Path); Assert.Equal(N, img.GetFrameCount(FrameDimension.Time)); Assert.Equal((W, H), (img.Width, img.Height));
            for (int i = 0; i < N; i++) { img.SelectActiveFrame(FrameDimension.Time, i); using var bmp = new Bitmap(img); var (m, _, _) = Compare(bmp, Frame(i), W, H); Assert.True(m < 12, $"frame {i}: mean abs error {m:F1} per channel"); }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(60, 2)]
    [InlineData(25, 4)]
    [InlineData(1, 100)]
    public void Gif_delay_is_rounded_centiseconds_clamped_to_two(int fps, int expected)
    {
        var dir = Tmp(); try
        {
            var r = Make(VideoFormat.Gif, Path.Combine(dir, "d.gif"), 32, 32, fps); using (var s = r.Sink) { s.WriteFrame(Frame(0, 32, 32)); s.Complete(); }
            var b = File.ReadAllBytes(r.Path); int i = Array.IndexOf(b, (byte)0xF9, 13); Assert.Equal(expected, b[i + 3] | b[i + 4] << 8);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------------------------------------ AVI
    [Fact]
    public void Avi_has_mjpg_stream_index_and_five_decodable_jpegs()
    {
        var dir = Tmp(); try
        {
            var r = Make(VideoFormat.Avi, Path.Combine(dir, "a.avi")); Assert.Equal(".avi", Path.GetExtension(r.Path));
            using (var s = r.Sink) { for (int i = 0; i < N; i++) s.WriteFrame(Frame(i)); s.Complete(); }
            var b = File.ReadAllBytes(r.Path); uint U(int o) => BitConverter.ToUInt32(b, o); string T(int o) => Encoding.ASCII.GetString(b, o, 4);
            Assert.Equal("RIFF", T(0)); Assert.Equal((uint)(b.Length - 8), U(4)); Assert.Equal("AVI ", T(8)); Assert.Equal("LIST", T(12)); Assert.Equal("hdrl", T(20)); Assert.Equal("avih", T(24));
            Assert.Equal(1_000_000u / Fps, U(32)); Assert.Equal(N, (int)U(48)); Assert.Equal(1u, U(56)); Assert.Equal(W, (int)U(64)); Assert.Equal(H, (int)U(68));
            int sh = IndexOf(b, "strh"); Assert.Equal("vids", T(sh + 8)); Assert.Equal("MJPG", T(sh + 12)); Assert.Equal(1u, U(sh + 28)); Assert.Equal((uint)Fps, U(sh + 32)); Assert.Equal(N, (int)U(sh + 40));
            int sf = IndexOf(b, "strf"); Assert.Equal(W, (int)U(sf + 12)); Assert.Equal(H, (int)U(sf + 16)); Assert.Equal("MJPG", T(sf + 24));
            int movi = IndexOf(b, "movi"); int idx1 = IndexOf(b, "idx1"); Assert.Equal((uint)(N * 16), U(idx1 + 4));
            for (int i = 0; i < N; i++)
            {
                int e = idx1 + 8 + 16 * i; Assert.Equal("00dc", T(e)); Assert.Equal(0x10u, U(e + 4)); int off = (int)U(e + 8), size = (int)U(e + 12);
                int chunk = movi + off; Assert.Equal("00dc", T(chunk)); Assert.Equal((uint)size, U(chunk + 4)); Assert.Equal(0xFF, b[chunk + 8]); Assert.Equal(0xD8, b[chunk + 9]);
                using var ms = new MemoryStream(b, chunk + 8, size); using var bmp = new Bitmap(ms); var (m, w, h) = Compare(bmp, Frame(i), W, H); Assert.Equal((W, H), (w, h)); Assert.True(m < 8, $"frame {i}: mean abs error {m:F1}");
            }
            Assert.Equal(N, CountChunks(b, "00dc", movi, idx1));
        }
        finally { Directory.Delete(dir, true); }
    }

    static int IndexOf(byte[] b, string tag, int from = 0) { var t = Encoding.ASCII.GetBytes(tag); for (int i = from; i <= b.Length - 4; i++) if (b[i] == t[0] && b[i + 1] == t[1] && b[i + 2] == t[2] && b[i + 3] == t[3]) return i; return -1; }
    static int CountChunks(byte[] b, string id, int from, int to) { int n = 0, p = from + 4; while (p < to) { if (Encoding.ASCII.GetString(b, p, 4) == id) { n++; int len = BitConverter.ToInt32(b, p + 4); p += 8 + len + (len & 1); } else break; } return n; }

    // ------------------------------------------------------------------------------------------------ MP4 via ffmpeg
    [Fact]
    public void Mp4_through_ffmpeg_has_five_h264_frames_of_the_right_size_and_duration()
    {
        var ff = Ffmpeg(); if (ff == null) { Console.WriteLine("SKIP Mp4 test: ffmpeg.exe not found (QWLSI_FFMPEG / third_party\\ffmpeg)"); return; }
        var dir = Tmp(); try
        {
            var opt = new VideoEncodeOptions { FfmpegPath = ff }; var enc = FfmpegLocator.ProbeH264Encoder(ff); Assert.NotNull(enc); Console.WriteLine($"ffmpeg {FfmpegLocator.VersionString(ff)}; H.264 encoder chosen: {enc}");
            var r = Make(VideoFormat.Mp4, Path.Combine(dir, "a.mp4"), o: opt); Assert.Equal(VideoFormat.Mp4, r.Format); Assert.Null(r.FallbackNote); Assert.Contains("H.264", r.Sink.Description);
            using (var s = r.Sink) { for (int i = 0; i < N; i++) s.WriteFrame(Frame(i)); s.Complete(); }
            Assert.True(new FileInfo(r.Path).Length > 1000);
            var d = FfmpegLocator.Run(ff, $"-hide_banner -i \"{r.Path}\" -map 0:v:0 -f null -", 30000); Assert.True(d.ok, d.stderr);
            Assert.Matches(@"Video: h264", d.stderr); Assert.Matches($@"{W}x{H}", d.stderr); Assert.Matches(@"Duration: 00:00:00\.50", d.stderr);
            var fr = Regex.Matches(d.stderr, @"frame=\s*(\d+)"); Assert.Equal(N, int.Parse(fr[^1].Groups[1].Value));
            var d2 = FfmpegLocator.Run(ff, $"-v error -i \"{r.Path}\" -f null -", 30000); Assert.True(d2.ok && d2.stderr.Trim().Length == 0, d2.stderr);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Mp4_without_ffmpeg_falls_back_to_avi_with_a_note()
    {
        var dir = Tmp(); try
        {
            var r = Make(VideoFormat.Mp4, Path.Combine(dir, "a.mp4"), o: new VideoEncodeOptions { DisableFfmpeg = true }); Assert.Equal(VideoFormat.Avi, r.Format); Assert.Equal(".avi", Path.GetExtension(r.Path));
            Assert.Contains("ffmpeg.exe not found", r.FallbackNote); Assert.Contains("AVI", r.FallbackNote);
            using (var s = r.Sink) { s.WriteFrame(Frame(0)); s.Complete(); }
            Assert.True(File.Exists(r.Path)); Assert.False(File.Exists(Path.Combine(dir, "a.mp4")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Mp4_rejects_odd_dimensions_and_other_formats_accept_them()
    {
        var dir = Tmp(); try
        {
            var e = Assert.Throws<ArgumentException>(() => Make(VideoFormat.Mp4, Path.Combine(dir, "o.mp4"), 321, 240, o: new VideoEncodeOptions { DisableFfmpeg = true })); Assert.Contains("even", e.Message);
            Assert.Throws<ArgumentException>(() => Make(VideoFormat.Mp4, Path.Combine(dir, "o.mp4"), 320, 241));
            Assert.Throws<ArgumentOutOfRangeException>(() => Make(VideoFormat.Gif, Path.Combine(dir, "o.gif"), fps: 0));
            var r = Make(VideoFormat.Gif, Path.Combine(dir, "o.gif"), 33, 21); using (var s = r.Sink) { s.WriteFrame(Frame(0, 33, 21)); s.Complete(); } Assert.True(File.Exists(r.Path));
            Assert.Throws<ArgumentException>(() => { using var s = Make(VideoFormat.Avi, Path.Combine(dir, "w.avi")).Sink; s.WriteFrame(new byte[10]); });
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Disposing_without_Complete_removes_the_partial_output_for_every_sink()
    {
        var ff = Ffmpeg(); var dir = Tmp(); try
        {
            foreach (var f in new[] { VideoFormat.Gif, VideoFormat.Avi, VideoFormat.PngSequence, VideoFormat.Mp4 })
            {
                if (f == VideoFormat.Mp4 && ff == null) { Console.WriteLine("SKIP Mp4 cancel: no ffmpeg"); continue; }
                var path = Path.Combine(dir, "c_" + f + (f == VideoFormat.PngSequence ? "" : ".x")); var r = Make(f, path, o: new VideoEncodeOptions { FfmpegPath = ff });
                using (var s = r.Sink) { s.WriteFrame(Frame(0)); s.WriteFrame(Frame(1)); }
                if (f == VideoFormat.PngSequence) Assert.False(Directory.Exists(r.Path) && Directory.EnumerateFileSystemEntries(r.Path).Any(), "png sequence left behind"); else Assert.False(File.Exists(r.Path), f + " left " + r.Path);
            }
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Ffmpeg_locator_finds_version_and_a_working_encoder_and_honours_an_explicit_path()
    {
        var ff = Ffmpeg(); if (ff == null) { Console.WriteLine("SKIP locator test: no ffmpeg"); return; }
        Assert.Equal(Path.GetFullPath(ff), FfmpegLocator.Find(ff)); Assert.StartsWith("ffmpeg version", FfmpegLocator.VersionString(ff)); Assert.NotNull(FfmpegLocator.ProbeH264Encoder(ff));
        Assert.Equal(FfmpegLocator.ProbeH264Encoder(ff), FfmpegLocator.ProbeH264Encoder(ff));      // cached
        Assert.Null(FfmpegLocator.VersionString(Path.Combine(Path.GetTempPath(), "no_such_ffmpeg.exe")));
        Assert.Null(FfmpegLocator.ProbeH264Encoder(Path.Combine(Path.GetTempPath(), "no_such_ffmpeg.exe")));
    }

    // ------------------------------------------------------------------------------------------------ performance sanity
    [Fact]
    public void Performance_1080p_ten_frames_per_sink()
    {
        var ff = Ffmpeg(); var dir = Tmp(); var frames = Enumerable.Range(0, 3).Select(i => Frame(i, 1920, 1080)).ToArray(); long peak0 = Process.GetCurrentProcess().PeakWorkingSet64;
        try
        {
            foreach (var f in new[] { VideoFormat.PngSequence, VideoFormat.Gif, VideoFormat.Avi, VideoFormat.Mp4 })
            {
                if (f == VideoFormat.Mp4 && ff == null) continue;
                var sw = Stopwatch.StartNew(); var r = Make(f, Path.Combine(dir, "p_" + f), 1920, 1080, 10, new VideoEncodeOptions { FfmpegPath = ff });
                using (var s = r.Sink) { for (int i = 0; i < 10; i++) s.WriteFrame(frames[i % 3]); s.Complete(); }
                long size = f == VideoFormat.PngSequence ? Directory.GetFiles(r.Path).Sum(x => new FileInfo(x).Length) : new FileInfo(r.Path).Length;
                Console.WriteLine($"PERF {f,-12} 1920x1080, 10 frames: {sw.ElapsedMilliseconds,6} ms ({sw.ElapsedMilliseconds / 10} ms/frame), {size / 1024.0:F0} KB  [{r.Sink.Description}]");
                Assert.True(size > 0);
            }
            Console.WriteLine($"PERF peak working set of the test process: {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB (before: {peak0 / 1048576} MB)");
        }
        finally { Directory.Delete(dir, true); }
    }
}
