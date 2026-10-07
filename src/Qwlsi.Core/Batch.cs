namespace Qwlsi.Core;

public sealed record BatchProgress(int Index, int Count, string File, double FileFraction, string State);

/// <summary>Sequential batch: one frame at a time (memory), per-file errors do not stop the run, cancel stops promptly (checked every CG iteration).</summary>
public static class BatchRunner
{
    public static List<SummaryRow> Run(ReconSession s, IReadOnlyList<string> files, Action<ReconOutcome> onResult, Action<string, Exception>? onError = null, IProgress<BatchProgress>? progress = null, CancellationToken ct = default)
    {
        var rows = new List<SummaryRow>();
        for (int i = 0; i < files.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            string f = files[i]; int idx = i; progress?.Report(new BatchProgress(idx, files.Count, f, 0, "processing"));
            try
            {
                var oc = s.ProcessFile(f, new Progress<double>(p => progress?.Report(new BatchProgress(idx, files.Count, f, p, "processing"))), ct);
                rows.Add(new SummaryRow { File = Path.GetFileName(f), Mode = oc.Mode, Stats = oc.Result.Stats, Seconds = oc.Seconds, PeakMb = oc.PeakWorkingSetMb });
                onResult(oc);
            }
            catch (OperationCanceledException) { progress?.Report(new BatchProgress(idx, files.Count, f, 0, "cancelled")); break; }
            catch (Exception e) { rows.Add(new SummaryRow { File = Path.GetFileName(f), Error = e.Message }); onError?.Invoke(f, e); }
            progress?.Report(new BatchProgress(idx, files.Count, f, 1, "done"));
        }
        return rows;
    }
}
