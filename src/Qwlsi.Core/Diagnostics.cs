using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Qwlsi.Core;

/// <summary>Daily log file in %APPDATA%\QwlsiApp\logs (override with QWLSI_LOG_DIR). Thread-safe, never throws.</summary>
public static class AppLog
{
    static readonly object Gate = new(); static bool initialised; static int errors, warnings;
    public static string Dir => Environment.GetEnvironmentVariable("QWLSI_LOG_DIR") is { Length: > 0 } d ? d : Path.Combine(AppSettings.Dir, "logs");
    public static string CurrentFile => Path.Combine(Dir, $"qwlsi-{DateTime.Now:yyyyMMdd}.log");
    /// <summary>Number of Error entries written in this process (used by the self-test to detect swallowed exceptions).</summary>
    public static int ErrorCount => Volatile.Read(ref errors);
    public static int WarningCount => Volatile.Read(ref warnings);

    public static void Initialize()
    {
        lock (Gate)
        {
            if (initialised) return; initialised = true;
            try { Directory.CreateDirectory(Dir); foreach (var f in Directory.EnumerateFiles(Dir, "*.log")) if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-30)) File.Delete(f); } catch { }
        }
    }

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) { Interlocked.Increment(ref warnings); Write("WARN ", msg); }
    public static void Error(string msg, Exception? ex = null) { Interlocked.Increment(ref errors); Write("ERROR", ex == null ? msg : msg + Environment.NewLine + ex); }

    static void Write(string level, string msg)
    {
        try
        {
            Initialize(); var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [t{Environment.CurrentManagedThreadId}] {msg}{Environment.NewLine}";
            lock (Gate) { Directory.CreateDirectory(Dir); File.AppendAllText(CurrentFile, line, Encoding.UTF8); }
        }
        catch { }
    }

    /// <summary>Last `lines` lines of today's log (for the diagnostics report).</summary>
    public static string Tail(int lines)
    {
        try { lock (Gate) { var f = CurrentFile; if (!File.Exists(f)) return "(no log yet today)"; using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var sr = new StreamReader(fs); var all = sr.ReadToEnd().Split('\n'); return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines - 1))).TrimEnd(); } }
        catch (Exception e) { return "(log unreadable: " + e.Message + ")"; }
    }
}

/// <summary>State of the Intel MKL FFT library (loaded module, version, threads, tiny FFT self-check).</summary>
public static unsafe class MklInfo
{
    [DllImport("mkl_rt.3.dll")] static extern void MKL_Get_Version_String(byte* buf, int len);
    [DllImport("mkl_rt.3.dll")] static extern int MKL_Get_Max_Threads();

    public static string Describe()
    {
        try
        {
            var sb = new StringBuilder(); byte* buf = stackalloc byte[256]; MKL_Get_Version_String(buf, 256);
            string ver = Marshal.PtrToStringAnsi((IntPtr)buf)?.Trim() ?? "?"; int thr = MKL_Get_Max_Threads();
            string? path = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().FirstOrDefault(m => m.ModuleName.StartsWith("mkl_rt", StringComparison.OrdinalIgnoreCase))?.FileName;
            sb.Append($"loaded - {ver}; max threads {thr}; module {path ?? "mkl_rt.3.dll"}; FFT self-check: {SelfCheck()}");
            return sb.ToString();
        }
        catch (Exception e) { return "NOT AVAILABLE (" + e.GetType().Name + ": " + e.Message + ") - FFT, and therefore every reconstruction, will fail"; }
    }

    static string SelfCheck()
    {
        try
        {
            using var fft = new ComplexFft2D(16); var a = new float[2 * 16 * 16]; a[2 * (3 * 16 + 5)] = 1f; fft.Forward(a); fft.Backward(a);
            float err = 0; for (int i = 0; i < a.Length; i++) err = Math.Max(err, Math.Abs(a[i] / 256f - (i == 2 * (3 * 16 + 5) ? 1f : 0f)));
            return err < 1e-5 ? "OK (16x16 round trip error " + err.ToString("G2") + ")" : "FAILED (error " + err + ")";
        }
        catch (Exception e) { return "FAILED (" + e.Message + ")"; }
    }
}
