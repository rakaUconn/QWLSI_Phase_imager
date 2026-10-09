using System.Drawing;
using System.Drawing.Imaging;

namespace Qwlsi.Video;

/// <summary>Reusable 32-bit bitmap that receives top-down BGRA frames (alpha is ignored by the encoders: JPEG / opaque PNG).</summary>
sealed class FrameBitmap : IDisposable
{
    public readonly int Width, Height; public readonly Bitmap Bmp;
    public FrameBitmap(int w, int h) { Width = w; Height = h; Bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb); }

    public void Load(ReadOnlySpan<byte> bgra)
    {
        if (bgra.Length != Width * Height * 4) throw new ArgumentException($"frame must be {Width}x{Height} BGRA = {Width * Height * 4} bytes, got {bgra.Length}");
        var d = Bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            int row = Width * 4;
            unsafe { for (int y = 0; y < Height; y++) bgra.Slice(y * row, row).CopyTo(new Span<byte>((byte*)d.Scan0 + (long)y * d.Stride, row)); }
        }
        finally { Bmp.UnlockBits(d); }
    }

    public void Dispose() => Bmp.Dispose();
}

abstract class SinkBase : IVideoSink
{
    public int Width { get; } public int Height { get; } public abstract string Description { get; }
    protected bool completed, disposed;
    protected SinkBase(int w, int h) { Width = w; Height = h; }
    protected void Check(ReadOnlySpan<byte> bgra)
    {
        if (disposed) throw new ObjectDisposedException(GetType().Name); if (completed) throw new InvalidOperationException("sink already completed");
        if (bgra.Length != Width * Height * 4) throw new ArgumentException($"frame must be {Width}x{Height} BGRA = {Width * Height * 4} bytes, got {bgra.Length}");
    }
    public abstract void WriteFrame(ReadOnlySpan<byte> bgra);
    public void Complete() { if (completed) return; Finish(); completed = true; }
    protected abstract void Finish();
    protected abstract void Cleanup(bool keep);
    public void Dispose() { if (disposed) return; disposed = true; Cleanup(completed); }
}
