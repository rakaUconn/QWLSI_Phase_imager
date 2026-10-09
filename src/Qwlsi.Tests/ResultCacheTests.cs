using Qwlsi.Core;

namespace Qwlsi.Tests;

/// <summary>1.0.1: every processed frame of a batch stays viewable (RAM keeps only 3, the rest comes from the disk cache or is recomputed).</summary>
[Collection("heavy")]
public class ResultCacheTests
{
    readonly GoldenFixture fx;
    public ResultCacheTests(GoldenFixture f) { fx = f; }
    string P(string n) => Path.Combine(fx.Root, n + ".tif");
    static string TempRoot() => Path.Combine(Path.GetTempPath(), "qwlsi_cache_test_" + Guid.NewGuid().ToString("N"));

    static ReconOutcome Synthetic(int M, int seed)
    {
        var rnd = new Random(seed); int n = M * M; var r = new ReconResult { M = M, Opd = new float[n], Intensity = new float[n], Mask = new byte[n], Stats = new WavefrontStats(seed, 1, 2, 3, 4, 5, 6) };
        for (int i = 0; i < n; i++) { bool ok = (i % M - M / 2) * (i % M - M / 2) + (i / M - M / 2) * (i / M - M / 2) < M * M / 5; r.Mask[i] = (byte)(ok ? 1 : 0); r.Opd[i] = ok ? (float)rnd.NextDouble() * 1e-6f : float.NaN; r.Intensity[i] = ok ? (float)rnd.NextDouble() : 0f; }
        return new ReconOutcome { File = $"synthetic_{seed}.tif", Mode = "without reference", Result = r, Seconds = seed, CarriersUsed = new Calibration { Fa = new[] { 0.1, 0.2 }, Fb = new[] { 0.3, -0.4 } }, Warnings = { "w" } };
    }

    static void AssertSame(ReconOutcome a, ReconOutcome b)
    {
        Assert.Equal(a.Result.M, b.Result.M); Assert.Equal(a.Result.Mask, b.Result.Mask); Assert.Equal(a.Result.Stats, b.Result.Stats);
        Assert.Equal(a.Result.Opd.Select(BitConverter.SingleToInt32Bits), b.Result.Opd.Select(BitConverter.SingleToInt32Bits));      // bit-exact incl. NaN
        Assert.Equal(a.Result.Intensity, b.Result.Intensity); Assert.Equal(a.Mode, b.Mode); Assert.Equal(a.Warnings, b.Warnings); Assert.Equal(a.CarriersUsed.Fa, b.CarriersUsed.Fa); Assert.Equal(a.CarriersUsed.Fb, b.CarriersUsed.Fb);
    }

    /// <summary>
    /// REGRESSION (1.0.0 bug): "Process all" with 5 frames, then click each row. Frame 1 had been evicted from the 3-result memory cache and the viewer said
    /// "No result yet". Mirrors OfflineViewModel: the batch callback stores into the ResultStore, a row click does store.Find / GetOrComputeAsync with the same key.
    /// </summary>
    [Fact]
    public async Task Process_five_frames_then_select_each_one_shows_a_result()
    {
        string root = TempRoot(); using var store = new ResultStore(new ResultDiskCache(root, 1L << 30), ramCount: 3);
        var frames = new[] { "concave_1_img", "concave_2_img", "concave_1-5_img", "convex_1_img", "convex_2_img" }.Select(P).ToList(); var batch = new Dictionary<string, ReconOutcome>();
        using (var s = new ReconSession { Par = fx.Par, Calibration = fx.Calib })
        {
            var rows = BatchRunner.Run(s, frames, oc => { store.Add(s.CacheKey(oc.File), oc); batch[oc.File] = oc; }, null, null, default);
            Assert.Equal(5, rows.Count); Assert.All(rows, r => Assert.Equal("", r.Error));
        }
        Assert.Equal(5, store.Disk.Count); Assert.True(store.Disk.TotalBytes > 5 * 15_000_000L);

        int computed = 0;      // "selecting" a row: the view model asks the store; a miss would trigger a recompute
        using var viewSession = new ReconSession { Par = fx.Par, Calibration = fx.Calib };
        foreach (var f in frames.Concat(frames.AsEnumerable().Reverse()))
        {
            var (oc, recomputed) = await store.GetOrComputeAsync(viewSession.CacheKey(f), ct => { computed++; return viewSession.ProcessFile(f, null, ct); });
            Assert.False(recomputed, Path.GetFileName(f) + " should come from RAM/disk, not be recomputed");
            Assert.NotNull(oc); Assert.Contains(oc.Result.Mask, m => m != 0); Assert.True(oc.Result.Stats.PupilRadiusMm > 4);
            AssertSame(batch[f], oc);                                 // what is shown is exactly what the batch produced
        }
        Assert.Equal(0, computed);
    }

    [Fact]
    public async Task Missing_result_is_recomputed_automatically_with_progress_and_agrees()
    {
        string root = TempRoot(); using var store = new ResultStore(new ResultDiskCache(root, 1L << 30), ramCount: 1);
        using var s = new ReconSession { Par = fx.Par, Calibration = fx.Calib }; string f = P("convex_1_img"); string key = s.CacheKey(f);
        var first = s.ProcessFile(f); store.Add(key, first);
        store.Clear();                                                // RAM and disk entry gone (evicted / cleaned)
        Assert.Null(store.Find(key));
        var seen = new List<double>(); var prog = new Progress<double>(p => { lock (seen) seen.Add(p); });
        var (oc, recomputed) = await store.GetOrComputeAsync(key, ct => s.ProcessFile(f, prog, ct));
        Assert.True(recomputed); await Task.Delay(200); lock (seen) Assert.NotEmpty(seen);
        double rms = GoldenFixture.RmsDiffNm(first.Result.Opd, oc.Result.Opd, first.Result.M, out _, out _, out _); Assert.True(rms < 0.01, $"recomputed OPD differs by {rms} nm rms");
        Assert.NotNull(store.Find(key));                              // and it is cached again
        var (_, again) = await store.GetOrComputeAsync(key, ct => throw new InvalidOperationException("must not recompute"));
        Assert.False(again);
    }

    [Fact]
    public async Task Cancelled_recompute_throws_and_leaves_nothing_cached()
    {
        string root = TempRoot(); using var store = new ResultStore(new ResultDiskCache(root, 1L << 30));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetOrComputeAsync("k", ct => { ct.ThrowIfCancellationRequested(); return Synthetic(64, 1); }, cts.Token));
        Assert.Null(store.Find("k"));
    }

    [Fact]
    public void Disk_cache_roundtrip_is_bit_exact_and_enforces_size_limit_and_cleans_up()
    {
        string root = TempRoot(); string dir;
        using (var cache = new ResultDiskCache(root, 50L << 20))      // 50 MB: room for 2 of the ~9 MB synthetic results... (1504^2 * 9 B = 20 MB) -> 2
        {
            dir = cache.Dir; var all = Enumerable.Range(1, 5).Select(i => Synthetic(1504, i)).ToList();
            for (int i = 0; i < 5; i++) { cache.Put("k" + i, all[i]); Assert.True(cache.TotalBytes <= 50L << 20, $"cache {cache.TotalBytes} bytes exceeds the limit"); }
            Assert.Equal(2, cache.Count); Assert.Null(cache.TryGet("k0")); Assert.Null(cache.TryGet("k2"));
            AssertSame(all[3], cache.TryGet("k3")!); AssertSame(all[4], cache.TryGet("k4")!);
            cache.TryGet("k3"); cache.Put("k5", Synthetic(1504, 9));       // k3 was used last -> k4 (least recently used) goes, k3 stays
            Assert.NotNull(cache.TryGet("k3")); Assert.Null(cache.TryGet("k4"));
            Assert.True(Directory.Exists(dir));
        }
        Assert.False(Directory.Exists(dir), "cache folder must be deleted on exit");
        Directory.Delete(root, true);
    }

    [Fact]
    public void Truncated_or_deleted_cache_file_is_a_miss_not_a_crash()
    {
        string root = TempRoot(); using var cache = new ResultDiskCache(root, 1L << 30); cache.Put("a", Synthetic(128, 1)); cache.Put("b", Synthetic(128, 2));
        var fa = Directory.GetFiles(cache.Dir, "a.qrc").Single(); using (var fs = new FileStream(fa, FileMode.Open, FileAccess.Write)) fs.SetLength(100);
        File.Delete(Path.Combine(cache.Dir, "b.qrc"));
        Assert.Null(cache.TryGet("a")); Assert.Null(cache.TryGet("b")); Assert.Equal(0, cache.Count);
        cache.Dispose(); Directory.Delete(root, true);
    }

    [Fact]
    public void Stale_session_folders_of_dead_processes_are_swept_but_live_ones_kept()
    {
        string root = TempRoot(); Directory.CreateDirectory(root);
        string dead = Path.Combine(root, "s2147480000_deadbeef"); Directory.CreateDirectory(dead); File.WriteAllText(Path.Combine(dead, "x.qrc"), "x");
        string live = Path.Combine(root, $"s{Environment.ProcessId}_cafebabe"); Directory.CreateDirectory(live);
        ResultDiskCache.SweepStale(root);
        Assert.False(Directory.Exists(dead)); Assert.True(Directory.Exists(live)); Directory.Delete(root, true);
    }

    [Fact]
    public void Cache_key_follows_every_setting_that_changes_the_result()
    {
        var par = fx.Par.Clone(); using var a = new ReconSession { Par = par, Calibration = fx.Calib }; string f = P("concave_1_img"), k = a.CacheKey(f);
        using var same = new ReconSession { Par = par.Clone(), Calibration = fx.Calib }; Assert.Equal(k, same.CacheKey(f));
        Assert.NotEqual(k, a.CacheKey(P("concave_2_img")));
        using var lam = new ReconSession { Par = new QwlsiParams { Lambda = 700e-9 }, Calibration = fx.Calib }; Assert.NotEqual(k, lam.CacheKey(f));
        using var cal = new ReconSession { Par = par, Calibration = new Calibration { Fa = new[] { fx.Calib.Fa[0] + 1e-6, fx.Calib.Fa[1] }, Fb = fx.Calib.Fb } }; Assert.NotEqual(k, cal.CacheKey(f));
        using var rf = new ReconSession { Par = par, Calibration = fx.Calib, ReferencePath = P("ref") }; Assert.NotEqual(k, rf.CacheKey(f));
        using var sw = new ReconSession { Par = par, Calibration = fx.Calib, SystemWavefrontPath = Path.Combine(fx.Results, "ref_OPD_m.npy") }; Assert.NotEqual(k, sw.CacheKey(f));
    }

    /// <summary>
    /// The in-app calibration (and a hand-edited calibration.json) can label the two orthogonal carriers the other way round: (fa', fb') = (fb, -fa). The nominal-carrier search
    /// takes the stronger of the two kx>0 peaks as "a" and defines b = a rotated by -90 deg; which of the two peaks is stronger depends on the frame. Both labellings must give the same wavefront.
    /// </summary>
    [Fact]
    public void Carrier_labelling_fa_fb_swapped_and_sign_flipped_gives_the_same_wavefront()
    {
        var c = fx.Calib; var swapped = new Calibration { Fa = (double[])c.Fb.Clone(), Fb = new[] { -c.Fa[0], -c.Fa[1] } };
        using var s = new ReconSession { Par = fx.Par, Calibration = swapped }; var oc = s.ProcessFile(P("concave_1_img"));
        double toGolden = GoldenFixture.RmsDiffNm(oc.Result.Opd, fx.GoldenOpd("concave_1_img"), 1504, out _, out _, out _);
        double toSameCalib = GoldenFixture.RmsDiffNm(oc.Result.Opd, fx.Recon("concave_1_img").Opd, 1504, out _, out _, out _);
        Console.WriteLine($"CARRIER RELABEL (fb,-fa): OPD rms difference to Python golden {toGolden:F4} nm, to calibration.json labelling {toSameCalib:F4} nm; power {oc.Result.Stats.PowerD:F4} D vs {fx.Recon("concave_1_img").Stats.PowerD:F4} D");
        Assert.True(toGolden < 5, $"{toGolden} nm");
    }
}
