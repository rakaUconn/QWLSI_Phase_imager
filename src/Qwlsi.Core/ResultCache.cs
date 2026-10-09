using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Qwlsi.Core;

/// <summary>
/// Per-session disk cache of processed results (OPD + intensity float32, mask byte, plus the small metadata), so every processed frame of a batch
/// can be shown again without keeping it in RAM. One sub-folder per process under the cache root; size-limited (least recently used entries go
/// first); the folder is deleted on <see cref="Dispose"/> and folders left behind by crashed sessions are swept on start.
/// </summary>
public sealed class ResultDiskCache : IDisposable
{
    public string Dir { get; }
    public long MaxBytes { get; }
    readonly object gate = new();
    readonly Dictionary<string, (long size, long used)> entries = new(); long tick, total;
    const string Magic = "QRC1";

    public ResultDiskCache(string root, long maxBytes)
    {
        MaxBytes = Math.Max(maxBytes, 1 << 20);
        SweepStale(root);
        Dir = Path.Combine(root, $"s{Environment.ProcessId}_{Guid.NewGuid():N}".Substring(0, 24));
        Directory.CreateDirectory(Dir);
    }

    public long TotalBytes { get { lock (gate) return total; } }
    public int Count { get { lock (gate) return entries.Count; } }
    string PathOf(string key) => Path.Combine(Dir, key + ".qrc");

    /// <summary>Deletes session folders of processes that are gone (crash / kill) or untouched for a week.</summary>
    public static void SweepStale(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.EnumerateDirectories(root, "s*_*"))
            {
                try
                {
                    var name = System.IO.Path.GetFileName(d); int us = name.IndexOf('_'); bool alive = false;
                    if (us > 1 && int.TryParse(name.AsSpan(1, us - 1), out int pid)) { try { using var p = Process.GetProcessById(pid); alive = !p.HasExited; } catch { alive = false; } }
                    if (!alive || (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d)).TotalDays > 7) Directory.Delete(d, true);
                }
                catch { }
            }
        }
        catch { }
    }

    public bool Contains(string key) { lock (gate) return entries.ContainsKey(key); }

    public void Put(string key, ReconOutcome oc)
    {
        var r = oc.Result; long n = (long)r.M * r.M; string path = PathOf(key), tmp = path + ".tmp";
        var meta = new Meta
        {
            File = oc.File, Mode = oc.Mode, Seconds = oc.Seconds, PeakWorkingSetMb = oc.PeakWorkingSetMb, PeakPrivateMb = oc.PeakPrivateMb, Carriers = oc.CarriersUsed,
            SystemWavefrontFile = oc.SystemWavefrontFile, ReferenceFile = oc.ReferenceFile, Width = oc.Width, Warnings = oc.Warnings, M = r.M, Stats = r.Stats, Solve = r.Solve,
            BlackLevel = r.BlackLevel, TimingsMs = r.TimingsMs,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(meta);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            fs.Write(Encoding.ASCII.GetBytes(Magic)); fs.Write(BitConverter.GetBytes(json.Length)); fs.Write(json);
            fs.Write(MemoryMarshal.AsBytes(r.Opd.AsSpan(0, (int)n))); fs.Write(MemoryMarshal.AsBytes(r.Intensity.AsSpan(0, (int)n))); fs.Write(r.Mask.AsSpan(0, (int)n));
        }
        long size = new FileInfo(tmp).Length;
        lock (gate)
        {
            File.Move(tmp, path, true);
            if (entries.TryGetValue(key, out var old)) total -= old.size;
            entries[key] = (size, ++tick); total += size;
            while (total > MaxBytes && entries.Count > 1)
            {   // evict the least recently used entry, never the one just written
                var victim = entries.Where(e => e.Key != key).OrderBy(e => e.Value.used).First();
                try { File.Delete(PathOf(victim.Key)); } catch { }
                total -= victim.Value.size; entries.Remove(victim.Key);
            }
        }
    }

    public ReconOutcome? TryGet(string key)
    {
        lock (gate) { if (!entries.ContainsKey(key)) return null; }
        try
        {
            using var fs = new FileStream(PathOf(key), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var head = new byte[8]; fs.ReadExactly(head); if (Encoding.ASCII.GetString(head, 0, 4) != Magic) throw new InvalidDataException("bad cache file");
            var json = new byte[BitConverter.ToInt32(head, 4)]; fs.ReadExactly(json);
            var meta = JsonSerializer.Deserialize<Meta>(json) ?? throw new InvalidDataException("bad cache metadata");
            int n = meta.M * meta.M; var res = new ReconResult { M = meta.M, Opd = new float[n], Intensity = new float[n], Mask = new byte[n], Stats = meta.Stats, Solve = meta.Solve, BlackLevel = meta.BlackLevel, TimingsMs = meta.TimingsMs };
            fs.ReadExactly(MemoryMarshal.AsBytes(res.Opd.AsSpan())); fs.ReadExactly(MemoryMarshal.AsBytes(res.Intensity.AsSpan())); fs.ReadExactly(res.Mask);
            lock (gate) { if (entries.TryGetValue(key, out var e)) entries[key] = (e.size, ++tick); }
            return new ReconOutcome
            {
                File = meta.File, Mode = meta.Mode, Result = res, Seconds = meta.Seconds, PeakWorkingSetMb = meta.PeakWorkingSetMb, PeakPrivateMb = meta.PeakPrivateMb, CarriersUsed = meta.Carriers,
                SystemWavefrontFile = meta.SystemWavefrontFile, ReferenceFile = meta.ReferenceFile, Width = meta.Width, Warnings = meta.Warnings,
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {   // missing / truncated / deleted behind our back: forget it, the caller recomputes
            lock (gate) { if (entries.Remove(key, out var old)) total -= old.size; }
            return null;
        }
    }

    public void Clear()
    {
        lock (gate) { foreach (var k in entries.Keys) try { File.Delete(PathOf(k)); } catch { } entries.Clear(); total = 0; }
    }

    public void Dispose()
    {
        lock (gate) { entries.Clear(); total = 0; }
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
    }

    sealed class Meta
    {
        public string File { get; set; } = ""; public string Mode { get; set; } = ""; public double Seconds { get; set; }
        public double PeakWorkingSetMb { get; set; } public double PeakPrivateMb { get; set; } public Calibration Carriers { get; set; } = new();
        public string? SystemWavefrontFile { get; set; } public string? ReferenceFile { get; set; } public int Width { get; set; } public List<string> Warnings { get; set; } = new();
        public int M { get; set; } public WavefrontStats Stats { get; set; } = new(0, 0, 0, 0, 0, 0, 0); public SolveInfo Solve { get; set; } = new(Array.Empty<int>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>());
        public double BlackLevel { get; set; } public Dictionary<string, double> TimingsMs { get; set; } = new();
    }
}

/// <summary>
/// What a result is looked up by: the frame (path, size, time stamp) and everything that changes the numbers (parameters, calibration or
/// reference / system wavefront, solver options). A changed setting therefore never shows a stale result.
/// </summary>
public static class ResultKey
{
    public static string Make(ReconSession s, string path)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture; var sb = new StringBuilder();
        void Stamp(string? p) { if (string.IsNullOrEmpty(p)) { sb.Append("-|"); return; } try { var fi = new FileInfo(p); sb.Append(Path.GetFullPath(p).ToLowerInvariant()).Append('|').Append(fi.Exists ? fi.Length : -1).Append('|').Append(fi.Exists ? fi.LastWriteTimeUtc.Ticks : -1).Append('|'); } catch { sb.Append(p).Append('|'); } }
        Stamp(path);
        var p0 = s.Par; sb.Append(string.Join(",", p0.Gamma.ToString("R", inv), p0.D.ToString("R", inv), p0.P.ToString("R", inv), p0.Z.ToString("R", inv), p0.Lambda.ToString("R", inv), p0.R, p0.N, p0.Dec)).Append('|');
        if (s.ReferencePath != null) { sb.Append("ref|"); Stamp(s.ReferencePath); }
        else
        {
            sb.Append("cal|"); var c = s.Calibration; if (c != null) sb.Append(string.Join(",", c.Fa.Concat(c.Fb).Select(v => v.ToString("R", inv)))).Append('|');
            Stamp(s.SystemWavefrontPath);
        }
        sb.Append(s.Solve.Iters).Append(',').Append(s.Solve.UnwrapPasses).Append(',').Append(s.Solve.Rtol.ToString("R", inv));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }
}

/// <summary>
/// Memory (last few results) -> disk cache -> recompute. The single place that answers "give me the result of this frame"; the Offline, Zernike and
/// 3-D views all get their data through it. Thread-safe.
/// </summary>
public sealed class ResultStore : IDisposable
{
    public ResultDiskCache Disk { get; }
    public int RamCount { get; }
    readonly List<(string key, ReconOutcome oc)> ram = new(); readonly object gate = new();

    public ResultStore(ResultDiskCache disk, int ramCount = 3) { Disk = disk; RamCount = Math.Max(1, ramCount); }

    /// <summary>Remembers a fresh result (RAM + disk). A disk failure (disk full, ...) is not fatal: the result then lives in RAM only.</summary>
    public void Add(string key, ReconOutcome oc)
    {
        try { Disk.Put(key, oc); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Warn("result cache write failed: " + e.Message); }
        lock (gate) { ram.RemoveAll(c => c.key == key); ram.Add((key, oc)); while (ram.Count > RamCount) ram.RemoveAt(0); }
    }

    /// <summary>RAM, then disk. null = not stored (never processed, evicted by the size limit, or the files are gone).</summary>
    public ReconOutcome? Find(string key)
    {
        lock (gate) { int i = ram.FindIndex(c => c.key == key); if (i >= 0) { var hit = ram[i]; ram.RemoveAt(i); ram.Add(hit); return hit.oc; } }
        var oc = Disk.TryGet(key); if (oc != null) lock (gate) { ram.RemoveAll(c => c.key == key); ram.Add((key, oc)); while (ram.Count > RamCount) ram.RemoveAt(0); }
        return oc;
    }

    /// <summary>Find, or recompute with <paramref name="compute"/> (run on the thread pool, cancellable) and remember. <paramref name="recomputed"/> tells which happened.</summary>
    public async Task<(ReconOutcome oc, bool recomputed)> GetOrComputeAsync(string key, Func<CancellationToken, ReconOutcome> compute, CancellationToken ct = default)
    {
        var hit = await Task.Run(() => Find(key), ct).ConfigureAwait(false); if (hit != null) return (hit, false);
        var oc = await Task.Run(() => compute(ct), ct).ConfigureAwait(false); Add(key, oc); return (oc, true);
    }

    public void Clear() { lock (gate) ram.Clear(); Disk.Clear(); }
    public void Dispose() { lock (gate) ram.Clear(); Disk.Dispose(); }
}
