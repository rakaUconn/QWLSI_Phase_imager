namespace Qwlsi.Video;

public enum VideoFormat { Mp4, Gif, Avi, PngSequence }

/// <summary>Encoder settings shared by all sinks.</summary>
public sealed class VideoEncodeOptions
{
    /// <summary>Path of ffmpeg.exe; null = look for it (app folder\ffmpeg\ffmpeg.exe, then PATH). See FfmpegLocator.</summary>
    public string? FfmpegPath { get; set; }
    /// <summary>H.264 target bits per pixel per frame (bitrate = w*h*fps*BitsPerPixel).</summary>
    public double BitsPerPixel { get; set; } = 0.10;
    /// <summary>JPEG quality 1..100 for AVI (MJPEG).</summary>
    public int JpegQuality { get; set; } = 90;
    /// <summary>GIF: 0 = loop forever.</summary>
    public int GifLoops { get; set; }
}

/// <summary>Receives rendered frames (top-down BGRA, Width*Height*4 bytes) and writes one output. Thread-confined: call from one thread.</summary>
public interface IVideoSink : IDisposable
{
    int Width { get; }
    int Height { get; }
    /// <summary>Human-readable description of what is being written, e.g. "MP4 (H.264, h264_mf)".</summary>
    string Description { get; }
    void WriteFrame(ReadOnlySpan<byte> bgra);
    /// <summary>Finalise the file(s). Dispose without Complete = cancelled: partial output is deleted.</summary>
    void Complete();
}

/// <summary>Result of <see cref="VideoSinkFactory.Create"/>: the sink and, when the requested format was not available, what was used instead and why.</summary>
public sealed record SinkResult(IVideoSink Sink, VideoFormat Format, string Path, string? FallbackNote);

// Implemented in VideoSinkFactory.cs by the encoder work: MP4 via ffmpeg (H.264: libopenh264 / h264_mf / h264_nvenc / h264_qsv / h264_amf, probed), GIF / AVI(MJPEG) / PNG sequence natively;
// MP4 without a working ffmpeg falls back to native AVI (MJPEG) with FallbackNote set. For PngSequence `path` is a folder (files frame_000001.png ...); other formats: output file path
// (the extension is corrected to the actual format).
//   public static class VideoSinkFactory { public static SinkResult Create(VideoFormat format, string path, int width, int height, int fps, VideoEncodeOptions? options = null); }
