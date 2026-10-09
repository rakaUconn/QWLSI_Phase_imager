using System.Drawing.Imaging;
using System.Text;

namespace Qwlsi.Video;

/// <summary>Native RIFF AVI with a 'MJPG' video stream (one JPEG per frame) and an idx1 index; opens in VLC, ffmpeg and Windows players with an MJPEG codec.</summary>
sealed class AviMjpegSink : SinkBase
{
    readonly string path; readonly FileStream fs; readonly BinaryWriter w; readonly FrameBitmap fb; readonly ImageCodecInfo jpeg; readonly EncoderParameters ep; readonly MemoryStream ms = new();
    readonly List<(uint offset, uint size)> index = new(); long moviStart, moviSizePos, riffSizePos, avihTotalPos, strhLengthPos; uint maxChunk; const long Limit = 1_900_000_000L;

    public AviMjpegSink(string file, int width, int height, int fps, int quality) : base(width, height)
    {
        path = file; fb = new FrameBitmap(width, height);
        jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid); ep = new EncoderParameters(1); ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
        fs = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20); w = new BinaryWriter(fs, Encoding.ASCII, true); Header(width, height, fps);
    }

    public override string Description => $"AVI (MJPEG, {Width}x{Height})";
    void Tag(string s) => w.Write(Encoding.ASCII.GetBytes(s));

    void Header(int W, int H, int fps)
    {
        Tag("RIFF"); riffSizePos = fs.Position; w.Write(0u); Tag("AVI ");
        Tag("LIST"); w.Write(4u + 8 + 56 + 12 + 8 + 56 + 8 + 40); Tag("hdrl");              // LIST hdrl: avih + LIST strl(strh + strf)
        Tag("avih"); w.Write(56u); w.Write((uint)(1_000_000 / fps)); w.Write(0u); w.Write(0u); w.Write(0x10u);   // usec/frame, max bytes/s, padding, flags = HASINDEX
        avihTotalPos = fs.Position; w.Write(0u); w.Write(0u); w.Write(1u); w.Write((uint)(W * H * 3)); w.Write((uint)W); w.Write((uint)H); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u);
        Tag("LIST"); w.Write(4u + 8 + 56 + 8 + 40); Tag("strl");
        Tag("strh"); w.Write(56u); Tag("vids"); Tag("MJPG"); w.Write(0u); w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write(1u); w.Write((uint)fps); w.Write(0u);
        strhLengthPos = fs.Position; w.Write(0u); w.Write((uint)(W * H * 3)); w.Write(0xFFFFFFFFu); w.Write(0u); w.Write((short)0); w.Write((short)0); w.Write((short)W); w.Write((short)H);
        Tag("strf"); w.Write(40u); w.Write(40u); w.Write(W); w.Write(H); w.Write((ushort)1); w.Write((ushort)24); Tag("MJPG"); w.Write((uint)(W * H * 3)); w.Write(0); w.Write(0); w.Write(0u); w.Write(0u);
        Tag("LIST"); moviSizePos = fs.Position; w.Write(0u); moviStart = fs.Position; Tag("movi");
    }

    public override void WriteFrame(ReadOnlySpan<byte> bgra)
    {
        Check(bgra); fb.Load(bgra); ms.SetLength(0); fb.Bmp.Save(ms, jpeg, ep); int len = (int)ms.Length;
        if (fs.Position + len + index.Count * 16L + 1024 > Limit) throw new InvalidOperationException("AVI would exceed the ~1.9 GB limit of the classic AVI format: use fewer frames, a lower resolution or the MP4 / PNG-sequence format");
        index.Add(((uint)(fs.Position - moviStart), (uint)len)); Tag("00dc"); w.Write((uint)len); w.Write(ms.GetBuffer(), 0, len); if ((len & 1) != 0) w.Write((byte)0); maxChunk = Math.Max(maxChunk, (uint)len);
    }

    protected override void Finish()
    {
        long moviEnd = fs.Position; Tag("idx1"); w.Write((uint)(index.Count * 16)); foreach (var (o, s) in index) { Tag("00dc"); w.Write(0x10u); w.Write(o); w.Write(s); }
        long end = fs.Position; fs.Position = riffSizePos; w.Write((uint)(end - 8)); fs.Position = moviSizePos; w.Write((uint)(moviEnd - moviStart)); fs.Position = avihTotalPos; w.Write((uint)index.Count);
        fs.Position = avihTotalPos + 12; w.Write(maxChunk); fs.Position = strhLengthPos; w.Write((uint)index.Count); w.Write(maxChunk); fs.Position = end; w.Flush();
    }

    protected override void Cleanup(bool keep) { try { w.Dispose(); fs.Dispose(); } catch { } fb.Dispose(); ep.Dispose(); if (!keep) try { File.Delete(path); } catch { } }
}
