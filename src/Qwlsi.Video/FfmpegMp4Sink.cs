using System.Diagnostics;
using System.Text;

namespace Qwlsi.Video;

/// <summary>MP4 (H.264) through ffmpeg.exe as a child process: raw BGRA frames go to its stdin, the file is written by ffmpeg (separate executable, not linked).</summary>
sealed class FfmpegMp4Sink : SinkBase
{
    readonly string path, encoder; readonly Process proc; readonly Stream stdin; readonly StringBuilder err = new(); readonly Task errTask; bool broken;

    public FfmpegMp4Sink(string ffmpeg, string encoderName, string file, int w, int h, int fps, double bitsPerPixel) : base(w, h)
    {
        path = file; encoder = encoderName; long bps = Math.Max(300_000L, (long)(w * (double)h * fps * bitsPerPixel));
        var args = $"-hide_banner -loglevel error -y -f rawvideo -pix_fmt bgra -s {w}x{h} -r {fps} -i - -an -vf format=yuv420p -c:v {encoderName} -b:v {bps} -maxrate {bps * 3 / 2} -bufsize {bps * 2} -g {Math.Max(1, fps * 2)} -movflags +faststart \"{file}\"";
        var psi = new ProcessStartInfo(ffmpeg, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
        proc = Process.Start(psi) ?? throw new IOException("could not start ffmpeg"); stdin = proc.StandardInput.BaseStream;
        errTask = Task.Run(() => { try { string? line; while ((line = proc.StandardError.ReadLine()) != null) lock (err) { if (err.Length < 20000) err.AppendLine(line); } } catch { } });
    }

    public override string Description => $"MP4 (H.264, {encoder}, {Width}x{Height})";
    string Tail() { lock (err) { var s = err.ToString().Trim(); return s.Length > 1500 ? "..." + s[^1500..] : s; } }

    public override void WriteFrame(ReadOnlySpan<byte> bgra)
    {
        Check(bgra);
        try { stdin.Write(bgra); }
        catch (IOException e) { broken = true; errTask.Wait(2000); throw new IOException("ffmpeg stopped while writing frames: " + (Tail() is { Length: > 0 } t ? t : e.Message), e); }
    }

    protected override void Finish()
    {
        try { stdin.Flush(); stdin.Close(); } catch (IOException) { }
        proc.WaitForExit(); errTask.Wait(5000);
        if (proc.ExitCode != 0 || broken) throw new IOException($"ffmpeg ({encoder}) failed with exit code {proc.ExitCode}: {Tail()}");
        if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new IOException("ffmpeg produced no output file: " + Tail());
    }

    protected override void Cleanup(bool keep)
    {
        try { if (!proc.HasExited) { if (!keep) proc.Kill(true); else proc.WaitForExit(10000); } } catch { }
        try { stdin.Dispose(); } catch { } try { proc.Dispose(); } catch { }
        if (!keep) for (int i = 0; i < 20; i++) { try { if (File.Exists(path)) File.Delete(path); break; } catch (IOException) { Thread.Sleep(50); } }
    }
}
