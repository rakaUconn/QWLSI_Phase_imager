namespace Qwlsi.Camera;

/// <summary>A frame leased from a source's buffer ring. Dispose to return the buffer. Data is float32, row-major, Width*Height.</summary>
public sealed class FrameLease : IDisposable
{
    readonly Action? release; int released;
    public float[] Data { get; } public int Width { get; } public int Height { get; } public long Index { get; } public DateTime Timestamp { get; }
    public FrameLease(float[] data, int w, int h, long index, DateTime ts, Action? release) { Data = data; Width = w; Height = h; Index = index; Timestamp = ts; this.release = release; }
    public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) release?.Invoke(); }
}

/// <summary>
/// Camera abstraction. Sources keep ONLY the newest frame for the consumer (older ones are overwritten = dropped):
/// a slow consumer never builds a backlog.
/// </summary>
public interface ICameraSource : IDisposable
{
    string Name { get; }
    int Width { get; }
    int Height { get; }
    bool IsRunning { get; }
    void Start();
    void Stop();
    /// <summary>Blocks until a frame with Index &gt; afterIndex exists (returns the NEWEST one) or the timeout/cancel hits (returns null).</summary>
    FrameLease? WaitNewest(long afterIndex, int timeoutMs, CancellationToken ct = default);
    /// <summary>Frames produced and frames overwritten before any consumer took them.</summary>
    (long produced, long overwritten) Counters { get; }
}
