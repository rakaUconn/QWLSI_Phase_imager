namespace Qwlsi.Video;

/// <summary>Creates the output sink for a format. MP4 = H.264 through ffmpeg.exe (bundled LGPL build); GIF, AVI (MJPEG) and PNG sequence are written natively. MP4 without a working ffmpeg falls back to AVI.</summary>
public static class VideoSinkFactory
{
    public static string Extension(VideoFormat f) => f switch { VideoFormat.Mp4 => ".mp4", VideoFormat.Gif => ".gif", VideoFormat.Avi => ".avi", _ => "" };

    /// <param name="path">output file (extension is corrected to the actual format); for <see cref="VideoFormat.PngSequence"/> a folder (frame_000001.png ...).</param>
    public static SinkResult Create(VideoFormat format, string path, int width, int height, int fps, VideoEncodeOptions? options = null)
    {
        options ??= new VideoEncodeOptions();
        if (width < 2 || height < 2) throw new ArgumentException($"video size {width}x{height} is too small");
        if (fps < 1 || fps > 120) throw new ArgumentOutOfRangeException(nameof(fps), "fps must be between 1 and 120");
        string? note = null;
        if (format == VideoFormat.Mp4)
        {
            if ((width & 1) != 0 || (height & 1) != 0) throw new ArgumentException($"MP4 (H.264, yuv420p) needs even width and height, got {width}x{height}");
            string? ff = options.DisableFfmpeg ? null : FfmpegLocator.Find(options.FfmpegPath);
            if (ff == null) { note = "ffmpeg.exe not found (looked in the ffmpeg folder next to the program, QWLSI_FFMPEG and PATH): saved as AVI (MJPEG) instead of MP4."; format = VideoFormat.Avi; }
            else
            {
                string? enc = FfmpegLocator.ProbeH264Encoder(ff);
                if (enc == null) { note = $"no working H.264 encoder in {ff} (tried the Media Foundation, NVENC, OpenH264, Quick Sync and AMF encoders): saved as AVI (MJPEG) instead of MP4."; format = VideoFormat.Avi; }
                else
                {
                    string p = Path.ChangeExtension(path, ".mp4"); Prepare(p);
                    return new SinkResult(new FfmpegMp4Sink(ff, enc, p, width, height, fps, options.BitsPerPixel), VideoFormat.Mp4, p, null);
                }
            }
        }
        switch (format)
        {
            case VideoFormat.Gif: { var p = Path.ChangeExtension(path, ".gif"); Prepare(p); return new SinkResult(new GifSink(p, width, height, fps, options.GifLoops), format, p, note); }
            case VideoFormat.Avi: { var p = Path.ChangeExtension(path, ".avi"); Prepare(p); return new SinkResult(new AviMjpegSink(p, width, height, fps, options.JpegQuality), format, p, note); }
            case VideoFormat.PngSequence: return new SinkResult(new PngSequenceSink(path, width, height), format, path, note);
            default: throw new ArgumentException("unknown format " + format);
        }
    }

    static void Prepare(string file) { var d = Path.GetDirectoryName(Path.GetFullPath(file)); if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d); }
}
