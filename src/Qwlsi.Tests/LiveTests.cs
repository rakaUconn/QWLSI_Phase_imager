using System.Diagnostics;
using Qwlsi.Camera;
using Qwlsi.Core;

namespace Qwlsi.Tests;

/// <summary>Live mode against SimulatorSource (lens frames + ref.tif replayed at 10 fps) — no camera needed.</summary>
[Collection("heavy")]
public class LiveTests
{
    readonly GoldenFixture fx;
    public LiveTests(GoldenFixture f) { fx = f; }

    string P(string n) => Path.Combine(fx.Root, n + ".tif");
    SimulatorSource AllFrames(double fps = 10) => new(GoldenFixture.Frames.Select(P), fps);

    [Fact]
    public void Simulator_delivers_frames_at_requested_rate_and_loops()
    {
        using var sim = AllFrames(); sim.Start(); long last = -1; var names = new HashSet<string>(); var sw = Stopwatch.StartNew(); int n = 0;
        while (sw.Elapsed.TotalSeconds < 5) { using var l = sim.WaitNewest(last, 500); if (l == null) continue; last = l.Index; n++; names.Add(sim.CurrentFrameName); }
        var (produced, _) = sim.Counters;
        Assert.InRange(produced / sw.Elapsed.TotalSeconds, 9.0, 10.5);
        Assert.True(n >= produced - 1, $"fast consumer should see every frame ({n}/{produced})");
        Assert.True(names.Count >= 4, "replay should cycle through the frames");
    }

    [Fact]
    public void Slow_consumer_gets_only_newest_frame_no_backlog()
    {
        using var sim = AllFrames(); sim.Start(); long last = -1, got = 0; var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 4)
        {
            using var l = sim.WaitNewest(last, 500); if (l == null) continue;
            Assert.True(l.Index > last); last = l.Index; got++;
            Assert.True(l.Index >= sim.Counters.produced - 1 - 1, $"stale frame {l.Index} handed over (produced {sim.Counters.produced})");   // always the NEWEST frame
            Thread.Sleep(350);   // consumer ~3x slower than the 10 fps source
        }
        var (produced, overwritten) = sim.Counters;
        Assert.InRange(got, 8, 14);   // ~1 per 350 ms
        Assert.True(overwritten > 0, "dropped frames expected");
    }

    [Fact]
    public void Live_transmittance_keeps_up_with_10fps_without_backlog()
    {
        using var sim = AllFrames(); using var live = new LivePipeline(sim, fx.Par); long frames = 0; float lastMean = 0; bool ratio = false;
        live.FrameReady += (map, isRatio, idx) => { frames++; ratio = isRatio; double s = 0; for (int i = 0; i < map.Length; i += 97) s += map[i]; lastMean = (float)s; };
        live.Start(); Thread.Sleep(10_000); var st = live.Stats; var (produced, _) = sim.Counters; live.Stop();
        Assert.False(ratio); Assert.True(frames > 0 && lastMean > 0);
        Assert.True(st.Processed >= produced * 0.9, $"processed {st.Processed} of {produced} produced");
        Assert.True(st.SkippedFrames <= produced * 0.1, $"skipped {st.SkippedFrames}");
        Assert.InRange(st.AvgProcessMs, 1, 100);   // must fit in the 100 ms frame period
    }

    [Fact]
    public void Live_with_reference_shows_ratio_near_one_on_the_reference_frame()
    {
        using var sim = new SimulatorSource(new[] { P("ref") }, 10); using var live = new LivePipeline(sim, fx.Par);
        live.SetReference(fx.Load("ref")); Assert.True(live.HasReference);
        double meanT = 0; bool isRatio = false; int got = 0;
        live.FrameReady += (map, r, idx) => { if (got++ > 0) return; isRatio = r; double s = 0; long c = 0; for (int i = 0; i < map.Length; i++) if (map[i] > 0) { s += map[i]; c++; } meanT = s / c; };
        live.Start(); Thread.Sleep(1500); live.Stop();
        Assert.True(isRatio); Assert.InRange(meanT, 0.999, 1.001);
    }

    [Fact]
    public async Task Capture_and_reconstruct_runs_full_solve_in_background_and_matches_golden()
    {
        using var sim = new SimulatorSource(new[] { P("concave_1_img") }, 10); using var live = new LivePipeline(sim, fx.Par);
        long frames = 0; live.FrameReady += (_, _, _) => Interlocked.Increment(ref frames);
        live.Start(); await Task.Delay(500);
        var prog = new List<double>(); var progress = new Progress<double>(p => { lock (prog) prog.Add(p); });
        long framesBefore = Interlocked.Read(ref frames); var sw = Stopwatch.StartNew();
        var res = await live.CaptureAndReconstruct(fx.Calib, nFrames: 3, progress: progress);
        double secs = sw.Elapsed.TotalSeconds; long during = Interlocked.Read(ref frames) - framesBefore; live.Stop();
        double rms = GoldenFixture.RmsDiffNm(res.Opd, fx.GoldenOpd("concave_1_img"), fx.Par.M, out _, out _, out _);
        Assert.True(rms <= 5.0, $"capture&reconstruct OPD rms vs golden {rms:F3} nm");
        Assert.Equal(new[] { 60, 60, 60 }, res.Solve.CgIterations);
        Assert.True(during >= 2, $"live view must keep running during the solve ({during} frames in {secs:F1}s)");
        await Task.Delay(100); lock (prog) { Assert.True(prog.Count > 20 && prog.Max() >= 1.0 - 1e-9); }
    }

    [Fact]
    public async Task Capture_and_reconstruct_can_be_cancelled()
    {
        using var sim = new SimulatorSource(new[] { P("concave_1_img") }, 10); using var live = new LivePipeline(sim, fx.Par);
        live.Start(); using var cts = new CancellationTokenSource(); var sw = Stopwatch.StartNew();
        var t = live.CaptureAndReconstruct(fx.Calib, 1, ct: cts.Token);
        await Task.Delay(1500); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
        Assert.True(sw.Elapsed.TotalSeconds < 4.0, $"cancel took too long ({sw.Elapsed.TotalSeconds:F1} s)");
        live.Stop();
    }

    /// <summary>HANDOFF §10: continuous 1-minute run, no memory growth, no backlog. Enable with QWLSI_LONG=1 (takes ~65 s).</summary>
    [Fact]
    public void One_minute_soak_no_memory_growth_no_backlog()
    {
        if (Environment.GetEnvironmentVariable("QWLSI_LONG") != "1") return;
        using var sim = AllFrames(); using var live = new LivePipeline(sim, fx.Par); live.Start();
        Thread.Sleep(10_000); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long mem0 = Process.GetCurrentProcess().PrivateMemorySize64, gc0 = GC.GetTotalMemory(true); var s0 = live.Stats; var (p0, _) = sim.Counters;
        Thread.Sleep(60_000);
        var s1 = live.Stats; var (p1, o1) = sim.Counters; GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long mem1 = Process.GetCurrentProcess().PrivateMemorySize64, gc1 = GC.GetTotalMemory(true); live.Stop();
        Console.WriteLine($"SOAK processed {s1.Processed - s0.Processed}/{p1 - p0} produced, skipped {s1.SkippedFrames - s0.SkippedFrames}, avg {s1.AvgProcessMs:F1} ms, private mem {mem0 / 1e6:F0} -> {mem1 / 1e6:F0} MB, managed {gc0 / 1e6:F0} -> {gc1 / 1e6:F0} MB");
        Assert.True(s1.Processed - s0.Processed >= (p1 - p0) * 0.9);
        Assert.True(mem1 - mem0 < 100_000_000, $"private memory grew by {(mem1 - mem0) / 1e6:F0} MB");
        Assert.True(gc1 - gc0 < 50_000_000, $"managed memory grew by {(gc1 - gc0) / 1e6:F0} MB");
    }
}
