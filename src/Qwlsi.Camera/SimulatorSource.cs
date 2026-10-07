using System.Diagnostics;
using Qwlsi.Core;

namespace Qwlsi.Camera;

/// <summary>Replays TIFF frames (e.g. the lens frames followed by ref.tif) in a loop at a fixed rate. Frames are held as uint16.</summary>
public sealed class SimulatorSource : ICameraSource
{
    const int Slots = 4;
    readonly List<ushort[]> frames = new(); readonly List<string> names = new();
    readonly double fps; readonly float[][] ring = new float[Slots][]; readonly int[] leases = new int[Slots];
    readonly object gate = new(); readonly bool loop;
    int latestSlot = -1; long latestIndex = -1, produced, overwritten; bool latestTaken = true;
    Thread? thread; volatile bool run;

    public string Name => "Simulator (" + string.Join(", ", names) + ")";
    public int Width { get; } public int Height { get; }
    public bool IsRunning => run;
    public (long produced, long overwritten) Counters { get { lock (gate) return (produced, overwritten); } }
    /// <summary>Name of the frame most recently produced (for display / tests).</summary>
    public string CurrentFrameName { get; private set; } = "";

    public SimulatorSource(IEnumerable<string> tiffPaths, double fps = 10, bool loop = true)
    {
        this.fps = fps; this.loop = loop; int w = 0, h = 0;
        foreach (var p in tiffPaths)
        {
            var f = TiffIO.ReadFrame(p, out w, out h); var u = new ushort[f.Length];
            for (long i = 0; i < f.Length; i++) u[i] = (ushort)f[i];
            if (frames.Count > 0 && (w != Width || h != Height)) throw new InvalidDataException("frame size mismatch: " + p);
            Width = w; Height = h; frames.Add(u); names.Add(Path.GetFileNameWithoutExtension(p));
        }
        if (frames.Count == 0) throw new ArgumentException("no frames");
        for (int i = 0; i < Slots; i++) ring[i] = new float[(long)Width * Height];
    }

    public void Start()
    {
        if (run) return; run = true;
        thread = new Thread(Produce) { IsBackground = true, Name = "SimulatorSource" }; thread.Start();
    }

    public void Stop() { run = false; thread?.Join(); thread = null; lock (gate) Monitor.PulseAll(gate); }

    void Produce()
    {
        var clock = Stopwatch.StartNew(); long tick = 0; int which = 0;
        while (run)
        {
            double due = tick / fps * 1000.0, now = clock.Elapsed.TotalMilliseconds;
            if (now < due) { Thread.Sleep((int)Math.Max(0, Math.Min(due - now, 5))); continue; }
            tick++;
            int slot = -1;
            lock (gate) { for (int i = 0; i < Slots; i++) if (i != latestSlot && leases[i] == 0) { slot = i; break; } }
            if (slot < 0) continue;   // all buffers busy: skip this tick (consumer is holding every slot)
            var src = frames[which]; var dst = ring[slot];
            Parallel.For(0, Height, y => { long o = (long)y * Width; for (int x = 0; x < Width; x++) dst[o + x] = src[o + x]; });
            lock (gate)
            {
                if (!latestTaken) overwritten++;
                latestSlot = slot; latestIndex++; produced++; latestTaken = false; CurrentFrameName = names[which];
                Monitor.PulseAll(gate);
            }
            which++; if (which >= frames.Count) { if (loop) which = 0; else { run = false; } }
        }
    }

    public FrameLease? WaitNewest(long afterIndex, int timeoutMs, CancellationToken ct = default)
    {
        var deadline = Stopwatch.StartNew();
        lock (gate)
        {
            while (latestIndex <= afterIndex)
            {
                if (ct.IsCancellationRequested || !run && latestIndex <= afterIndex) return null;
                int left = timeoutMs - (int)deadline.ElapsedMilliseconds; if (left <= 0) return null;
                Monitor.Wait(gate, Math.Min(left, 50));
            }
            int s = latestSlot; leases[s]++; latestTaken = true;
            return new FrameLease(ring[s], Width, Height, latestIndex, DateTime.UtcNow, () => { lock (gate) leases[s]--; });
        }
    }

    public void Dispose() => Stop();
}
