using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using Qwlsi.App.ViewModels;
using Qwlsi.Core;

namespace Qwlsi.App;

/// <summary>Version and run-time facts shared by the title bar, the About box and the crash reports.</summary>
public static class AppInfo
{
    public static string Version { get; } = (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
    public static string BuildDate { get; } = SafeBuildDate();
    static string SafeBuildDate() { try { return File.GetLastWriteTime(Assembly.GetExecutingAssembly().Location).ToString("yyyy-MM-dd HH:mm"); } catch { return "?"; } }
}

/// <summary>Mutable facts that only some part of the UI knows (3-D renderer, file being processed, last result).</summary>
public static class AppState
{
    public static string Renderer = "3-D viewer not opened in this session";
    public static string? CurrentFile;
    public static string? LastResult;
    public static string? LastCarriers;
}

public static class AppDiagnostics
{
    static string Mb(long bytes) => (bytes / 1048576.0).ToString("F0") + " MB";

    static string Gpus()
    {
        try
        {
            var sb = new StringBuilder(); using var f = new SharpDX.DXGI.Factory1();
            for (int i = 0; i < f.GetAdapterCount1(); i++) { using var a = f.GetAdapter1(i); var d = a.Description1; sb.Append($"{(sb.Length > 0 ? "; " : "")}{d.Description.Trim()} ({d.DedicatedVideoMemory / 1048576} MB VRAM)"); }
            return sb.Length > 0 ? sb.ToString() : "none reported";
        }
        catch (Exception e) { return "unavailable (" + e.Message + ")"; }
    }

    /// <summary>Plain-text report meant to be pasted into a bug report.</summary>
    public static string Build(MainViewModel? vm, bool includeSettings = true, int logLines = 40)
    {
        var sb = new StringBuilder(); var inv = System.Globalization.CultureInfo.InvariantCulture; var p = Process.GetCurrentProcess(); p.Refresh();
        sb.AppendLine("QWLSI single-frame wavefront - diagnostics");
        sb.AppendLine($"Version        : {AppInfo.Version} (built {AppInfo.BuildDate})");
        sb.AppendLine($"Generated      : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"OS             : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), {Environment.ProcessorCount} logical processors, {Mb(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)} RAM available to the process");
        sb.AppendLine($".NET runtime   : {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier}); app folder {AppContext.BaseDirectory}");
        sb.AppendLine($"MKL (FFT)      : {MklInfo.Describe()}");
        sb.AppendLine($"3-D rendering  : {AppState.Renderer}");
        sb.AppendLine($"Graphics       : {Gpus()}");
        if (vm != null)
        {
            var c = vm.Current; var pr = vm.Settings.Params;
            sb.AppendLine($"Calibration    : {(c == null ? "none loaded" : (vm.CurrentPath ?? "(unsaved, made in this session)") + $"  fa=[{c.Fa[0].ToString("R", inv)}, {c.Fa[1].ToString("R", inv)}] fb=[{c.Fb[0].ToString("R", inv)}, {c.Fb[1].ToString("R", inv)}] cyc/px" + (c.Source != null ? "  source: " + c.Source : ""))}");
            sb.AppendLine($"Carriers used  : {AppState.LastCarriers ?? "no frame processed yet"}");
            sb.AppendLine($"Parameters     : Gamma={pr.Gamma * 1e6:G6} um, d={pr.D * 1e3:G6} mm, p={pr.P * 1e6:G6} um, Z={pr.Z:G6}, lambda={pr.Lambda * 1e9:G6} nm, R={pr.R}, N={pr.N}, decimation={pr.Dec}");
            sb.AppendLine($"Last result    : {AppState.LastResult ?? "none"}");
            sb.AppendLine($"Frames in list : {vm.Offline.Files.Count}; mode: {(vm.Offline.WithReference ? "with reference (" + vm.Offline.ReferencePath + ")" : "without reference" + (string.IsNullOrEmpty(vm.Offline.SystemWavefrontPath) ? "" : ", system wavefront " + vm.Offline.SystemWavefrontPath))}");
        }
        sb.AppendLine($"Memory         : working set {Mb(p.WorkingSet64)}, private {Mb(p.PrivateMemorySize64)}, managed heap {Mb(GC.GetTotalMemory(false))}");
        sb.AppendLine($"Peak memory    : peak working set {Mb(p.PeakWorkingSet64)}, peak commit {Mb(p.PeakPagedMemorySize64)}" + (vm != null && vm.Offline.LastPeakMb > 0 ? $"; peak during the last frame {vm.Offline.LastPeakMb:F0} MB" : ""));
        sb.AppendLine($"Settings file  : {AppSettings.FilePath}");
        sb.AppendLine($"Log folder     : {AppLog.Dir}");
        sb.AppendLine($"Log entries    : {AppLog.ErrorCount} error(s), {AppLog.WarningCount} warning(s) in this session");
        if (includeSettings && vm != null)
        {
            sb.AppendLine(); sb.AppendLine("--- settings in use (settings.json) ---");
            try { sb.AppendLine(JsonSerializer.Serialize(vm.Settings, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })); } catch (Exception e) { sb.AppendLine("(settings could not be serialised: " + e.Message + ")"); }
        }
        if (logLines > 0) { sb.AppendLine(); sb.AppendLine($"--- last {logLines} log lines ---"); sb.AppendLine(AppLog.Tail(logLines)); }
        return sb.ToString();
    }
}

/// <summary>Writes crash-yyyyMMdd-HHmmss.txt (exception + full diagnostics + settings) next to the log files and offers to copy it.</summary>
public static class CrashReporter
{
    public static string? Write(Exception ex, string source, MainViewModel? vm)
    {
        string? path = null;
        try
        {
            AppLog.Error("UNHANDLED EXCEPTION in " + source, ex); Directory.CreateDirectory(AppLog.Dir);
            path = Path.Combine(AppLog.Dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"QWLSI single-frame wavefront {AppInfo.Version} - crash report"); sb.AppendLine($"Source   : {source}"); sb.AppendLine($"Time     : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Working on: {AppState.CurrentFile ?? "(no frame being processed)"}"); sb.AppendLine(); sb.AppendLine("--- exception ---"); sb.AppendLine(ex.ToString()); sb.AppendLine();
            sb.AppendLine(AppDiagnostics.Build(vm, includeSettings: true, logLines: 60));
            File.WriteAllText(path, sb.ToString());
        }
        catch { }
        return path;
    }

    /// <summary>Shows a short dialog; "Yes" copies the whole report to the clipboard so it can be pasted into a bug report.</summary>
    public static void Notify(Exception ex, string? reportPath, bool fatal)
    {
        try
        {
            var msg = (fatal ? "QwlsiApp hit an unexpected error and has to close." : "An unexpected error occurred. The program can continue, but please report it.") + "\n\n" + ex.GetType().Name + ": " + ex.Message +
                      (reportPath != null ? "\n\nA report was saved to:\n" + reportPath + "\n\nCopy the report to the clipboard now (to paste it into a bug report)?" : "");
            var r = MessageBox.Show(msg, "QWLSI wavefront " + AppInfo.Version, reportPath != null ? MessageBoxButton.YesNo : MessageBoxButton.OK, MessageBoxImage.Error);
            if (r == MessageBoxResult.Yes && reportPath != null) Clipboard.SetText(File.ReadAllText(reportPath));
        }
        catch { }
    }
}
