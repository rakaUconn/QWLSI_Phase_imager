using System.Diagnostics;
using Qwlsi.Core;

namespace Qwlsi.Camera;

public sealed record LiveStats(long Processed, long SkippedFrames, double AvgProcessMs, double Fps, bool HasReference);

/// <summary>
/// Live transmittance view (HANDOFF §5): worker thread takes ONLY the newest frame from the source, computes the DC-lobe
/// transmittance (T = I/I_ref with a reference, otherwise normalised intensity labelled relative) and publishes it.
/// Also hosts "Capture and reconstruct": average N frames, run the full 60x3 solve on a background task (progress + cancel).
/// </summary>
public sealed class LivePipeline : IDisposable
{
    readonly ICameraSource src; readonly QwlsiParams par; readonly TransmittanceProcessor tp; readonly int M; readonly long nM;
    readonly float[] I, T, scratch; float[]? Iref;
    Thread? worker; volatile bool run; long processed, skipped; double procMsSum; readonly Stopwatch wall = new();
    CancellationTokenSource? cts;

    /// <summary>Raised on the worker thread. `map` is only valid during the call (copy it). `isRatio`: true = T=I/Iref, false = relative intensity.</summary>
    public event Action<float[], bool, long>? FrameReady;

    public LivePipeline(ICameraSource source, QwlsiParams p)
    {
        src = source; par = p; M = p.M; nM = (long)M * M; tp = new TransmittanceProcessor(p);
        I = new float[nM]; T = new float[nM]; scratch = new float[nM];
    }

    public bool HasReference => Iref != null;
    public void ClearReference() => Iref = null;

    /// <summary>Stores the DC-lobe intensity of `frame` as the reference (call with an average for noise reduction).</summary>
    public void SetReference(float[] frame) { var r = new float[nM]; lock (tp) tp.DcIntensity(frame, r); Iref = r; }

    public LiveStats Stats
    {
        get { long n = Interlocked.Read(ref processed); double s = wall.Elapsed.TotalSeconds; return new(n, Interlocked.Read(ref skipped), n > 0 ? procMsSum / n : 0, s > 0 ? n / s : 0, Iref != null); }
    }

    public void Start()
    {
        if (run) return; run = true; wall.Restart(); src.Start();
        worker = new Thread(Loop) { IsBackground = true, Name = "LiveTransmittance" }; worker.Start();
    }

    public void Stop() { run = false; worker?.Join(); worker = null; src.Stop(); wall.Stop(); }

    void Loop()
    {
        long last = -1; var sw = new Stopwatch();
        while (run)
        {
            using var lease = src.WaitNewest(last, 200); if (lease == null) continue;
            if (last >= 0) Interlocked.Add(ref skipped, lease.Index - last - 1);   // frames that were overwritten before we got to them
            last = lease.Index; sw.Restart();
            lock (tp)
            {
                var r = Iref;
                if (r != null) { tp.DcIntensity(lease.Data, I); TransmittanceProcessor.Ratio(I, r, T, M, scratch); }
                else tp.Normalised(lease.Data, T);
                FrameReady?.Invoke(T, r != null, lease.Index);
            }
            procMsSum += sw.Elapsed.TotalMilliseconds; Interlocked.Increment(ref processed);
        }
    }

    /// <summary>
    /// "Capture and reconstruct": averages the next `nFrames` frames from the source and runs the full-accuracy solve
    /// (defaults 60 x 3 passes, identical to offline) on a background task. Live view keeps running. Cancel via `ct`.
    /// </summary>
    public Task<ReconResult> CaptureAndReconstruct(Calibration calib, int nFrames = 1, ReferenceData? reference = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var avg = new float[(long)src.Width * src.Height]; long last = -1; int got = 0;
            while (got < nFrames)
            {
                ct.ThrowIfCancellationRequested();
                using var lease = src.WaitNewest(last, 2000, ct) ?? throw new TimeoutException("no frame from camera");
                if (lease.Index == last) continue; last = lease.Index; got++;
                var d = lease.Data; for (long i = 0; i < avg.Length; i++) avg[i] += d[i];
                progress?.Report(0.1 * got / nFrames);
            }
            if (nFrames > 1) { float k = 1f / nFrames; for (long i = 0; i < avg.Length; i++) avg[i] *= k; }
            using var proc = new Processor(par);
            var opt = new SolveOptions { Cancel = ct, Progress = p => progress?.Report(0.1 + 0.9 * p) };
            var res = proc.Process(avg, calib, opt, reference);
            progress?.Report(1.0); return res;
        }, ct);
    }

    public void Dispose() { Stop(); tp.Dispose(); }
}
