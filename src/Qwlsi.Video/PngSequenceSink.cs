using System.Drawing.Imaging;

namespace Qwlsi.Video;

/// <summary>frame_000001.png, frame_000002.png, ... in a folder (path = folder).</summary>
sealed class PngSequenceSink : SinkBase
{
    readonly string dir; readonly bool createdDir; readonly FrameBitmap fb; int n; readonly List<string> files = new();
    public PngSequenceSink(string folder, int w, int h) : base(w, h) { dir = folder; createdDir = !Directory.Exists(folder); Directory.CreateDirectory(folder); fb = new FrameBitmap(w, h); }
    public override string Description => "PNG sequence";
    public override void WriteFrame(ReadOnlySpan<byte> bgra) { Check(bgra); fb.Load(bgra); var f = Path.Combine(dir, $"frame_{++n:D6}.png"); fb.Bmp.Save(f, ImageFormat.Png); files.Add(f); }
    protected override void Finish() { }
    protected override void Cleanup(bool keep)
    {
        fb.Dispose(); if (keep) return;
        foreach (var f in files) try { File.Delete(f); } catch { }
        if (createdDir) try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
    }
}
