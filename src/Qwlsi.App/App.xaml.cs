using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Qwlsi.App.ViewModels;
using Qwlsi.Core;

namespace Qwlsi.App;

public class InvBoolConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => !(v is bool b && b);
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => !(v is bool b && b);
}

public class UnitNameConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => v switch { Units.Nm => "nm", Units.Um => "µm", Units.M => "m", Units.Waves => "waves", _ => v?.ToString() ?? "" };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class BoolIndexConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => v is bool b && b ? 1 : 0;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => v is int i && i == 1;
}

public partial class App : Application
{
    public static bool SelfTestMode;
    static MainViewModel? mainVm;
    /// <summary>Deletes the temporary result cache (idempotent; called from OnExit and, as a backstop, from ProcessExit).</summary>
    static void CleanupCache() { try { mainVm?.Offline.Shutdown(); } catch { } }

    static MainViewModel? Vm => Current?.MainWindow?.DataContext as MainViewModel;

    /// <summary>Single place for every unhandled exception: log + crash report (exception, diagnostics, settings in use).</summary>
    static void Crash(Exception ex, string source, bool fatal, bool quiet = false)
    {
        if (quiet)
        {   // exceptions of the hosted Direct3D control: logged, shown in the status bar, no dialog
            AppLog.Error("ignored exception in " + source, ex);
            try { if (Vm != null) Vm.StatusMessage = "A 3-D view input error was ignored (details in the log: " + AppLog.CurrentFile + ")."; } catch { }
            return;
        }
        var path = CrashReporter.Write(ex, source, Vm);
        if (SelfTestMode) return;
        CrashReporter.Notify(ex, path, fatal);
    }

    public App()
    {
        AppLog.Initialize(); AppLog.Info($"QwlsiApp {AppInfo.Version} starting ({System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, {Environment.ProcessorCount} logical processors)");
        // The 3-D viewer lives in a WinForms host: its exceptions would otherwise pop up the modal WinForms crash dialog.
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, e) => Crash(e.Exception, "WinForms host thread", false, quiet: true);
        DispatcherUnhandledException += (_, e) => { Crash(e.Exception, "UI thread", false); e.Handled = true; if (SelfTestMode) Shutdown(2); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()), "AppDomain (fatal)", true);
        TaskScheduler.UnobservedTaskException += (_, e) => { AppLog.Error("unobserved task exception", e.Exception); e.SetObserved(); };
    }

    /// <summary>
    /// Helix leaves a FOREGROUND worker thread behind for every viewport / device it created, which keeps the process alive after the last
    /// window is closed. After a normal WPF shutdown a background watchdog ends the process if it is still running.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("QwlsiApp exiting (code " + e.ApplicationExitCode + ")"); CleanupCache();
        base.OnExit(e);
        var t = new Thread(() => { Thread.Sleep(2500); Environment.Exit(e.ApplicationExitCode); }) { IsBackground = true, Name = "ExitWatchdog" };
        t.Start();
    }

    void OnStartup(object sender, StartupEventArgs e)
    {
        var args = e.Args; string? Opt(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var selftest = Opt("--selftest"); SelfTestMode = selftest != null; if (selftest != null) { Directory.CreateDirectory(selftest); Environment.SetEnvironmentVariable("QWLSI_APPDATA", Path.Combine(selftest, "appdata")); }   // self-test never touches the real settings / logs
        var settings = selftest != null ? new AppSettings() : null;      // self-test: defaults, never touches the user's settings.json
        var vm = new MainViewModel(settings); mainVm = vm; AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupCache();
        var win = new MainWindow { DataContext = vm };
        if (selftest != null) win.ContentRendered += async (_, _) => await SelfTest.Run(win, vm, selftest, Opt("--file") ?? "", Opt("--ref") ?? "", Opt("--calib") ?? "", Opt("--flat") ?? "");
        win.Show();
    }
}

/// <summary>Scripted walk through the screens that saves a PNG of each (used for the Phase-1 screenshots and as a smoke test).</summary>
static class SelfTest
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    const uint LDOWN = 2, LUP = 4, RDOWN = 8, RUP = 0x10, WHEEL = 0x800;
    static async Task Drag(int x0, int y0, int x1, int y1, bool right)
    {
        SetCursorPos(x0, y0); await Task.Delay(80); mouse_event(right ? RDOWN : LDOWN, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80);
        for (int i = 1; i <= 12; i++) { SetCursorPos(x0 + (x1 - x0) * i / 12, y0 + (y1 - y0) * i / 12); await Task.Delay(25); }
        mouse_event(right ? RUP : LUP, 0, 0, 0, UIntPtr.Zero); await Task.Delay(150);
    }
    static async Task Click(int x, int y) { SetCursorPos(x, y); await Task.Delay(80); mouse_event(LDOWN, 0, 0, 0, UIntPtr.Zero); await Task.Delay(40); mouse_event(LUP, 0, 0, 0, UIntPtr.Zero); await Task.Delay(200); }

    public static async Task Run(MainWindow win, MainViewModel vm, string outDir, string file, string refFile, string calib, string flat)
    {
        Directory.CreateDirectory(outDir); var log = new List<string>(); File.WriteAllText(Path.Combine(outDir, "selftest.started"), DateTime.Now.ToString());
        void Shot(string name)
        {
            win.UpdateLayout(); var fe = (FrameworkElement)win.Content; int w = (int)fe.ActualWidth, h = (int)fe.ActualHeight;
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); rtb.Render(fe);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb)); using var fs = File.Create(Path.Combine(outDir, name + ".png")); enc.Save(fs);
        }
        void ShotScreen(string name)
        {
            win.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(win); var fe = (FrameworkElement)win.Content; var p = fe.PointToScreen(new Point(0, 0));
            int w = (int)(fe.ActualWidth * dpi.DpiScaleX), h = (int)(fe.ActualHeight * dpi.DpiScaleY);
            using var bmp = new System.Drawing.Bitmap(w, h); using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen((int)p.X, (int)p.Y, 0, 0, new System.Drawing.Size(w, h));
            bmp.Save(Path.Combine(outDir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        string Mem(string tag) { var p = System.Diagnostics.Process.GetCurrentProcess(); p.Refresh(); return $"MEM {tag}: WS {p.WorkingSet64 / 1048576} MB, private {p.PrivateMemorySize64 / 1048576} MB, managed {GC.GetTotalMemory(false) / 1048576} MB"; }
        Task Idle() => win.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
        try
        {
            win.Width = 1500; win.Height = 920; await Idle();
            if (calib != "") vm.SetCalibration(Qwlsi.Core.Calibration.Load(calib), calib);
            vm.SelectedTab = 0; await Idle(); Shot("01_offline_empty");
            {   // regression: mouse input on the EMPTY 3-D viewport (no result, no camera yet)
                vm.SelectedTab = 2; await Task.Delay(1500); await Idle(); win.Activate(); var dpi0 = VisualTreeHelper.GetDpi(win); var o0 = win.View3D.PointToScreen(new Point(0, 0));
                double W0 = win.View3D.ActualWidth * dpi0.DpiScaleX, H0 = win.View3D.ActualHeight * dpi0.DpiScaleY; string el = Path.Combine(Path.GetTempPath(), "qwlsi_error.log"); long b0 = Qwlsi.Core.AppLog.ErrorCount + Qwlsi.App.Controls.SafeViewport3DX.Swallowed;
                int ex = (int)(o0.X + W0 * 0.5), ey = (int)(o0.Y + H0 * 0.5); await Click(ex, ey); await Drag(ex, ey, ex + 100, ey + 40, false); await Drag(ex, ey, ex - 60, ey - 30, true);
                mouse_event(WHEEL, 0, 0, 120, UIntPtr.Zero); await Task.Delay(300); await Click((int)(o0.X + W0 - 70 * dpi0.DpiScaleX), (int)(o0.Y + 70 * dpi0.DpiScaleY)); await Task.Delay(500);
                long a0 = Qwlsi.Core.AppLog.ErrorCount + Qwlsi.App.Controls.SafeViewport3DX.Swallowed; log.Add("3D mouse on EMPTY viewport: exception count grew by " + (a0 - b0) + " (0 = no exception)"); vm.SelectedTab = 0; await Idle();
            }
            vm.Offline.AddPaths(file.Split(';', StringSplitOptions.RemoveEmptyEntries)); await Idle();
            await vm.Offline.ProcessAllCommand.ExecuteAsync(null); await Idle(); await Task.Delay(300); await Idle();
            log.Add("without reference: " + vm.Offline.StatsText.Replace("\n", " | ")); Shot("02_offline_opd");
            {   // 1.0.1 regression: after "Process all" the LAST frame is shown, every row can be selected and shows a result (RAM -> disk cache -> recompute)
                var off = vm.Offline; int nDone = off.Files.Count(f => f.Status == "done");
                log.Add($"REGRESS batch: {nDone}/{off.Files.Count} done; selected after batch = {off.SelectedFile?.Name} (last row = {off.Files.LastOrDefault()?.Name}); Spec shown = {off.Spec != null}; batch note = {(off.BatchNote.Length > 0 ? "yes" : "NO")}; export folder = {off.ExportFolder}");
                int ok = 0, bad = 0;
                foreach (var row in off.Files.Concat(off.Files.Reverse()).ToList())
                {
                    off.SelectedFile = null; off.SelectedFile = row; await off.ShowTask; await Idle();
                    bool shownOk = off.Spec != null && off.Shown != null && string.Equals(off.Shown.File, row.Path, StringComparison.OrdinalIgnoreCase) && off.StatsText.Contains("Lens power");
                    if (shownOk) ok++; else { bad++; log.Add("REGRESS row without result: " + row.Name + " -> " + off.StatsText); }
                }
                log.Add($"REGRESS select every row twice (forward + backward): {ok} shown, {bad} missing; disk cache entries {off.Store.Disk.Count}, {off.Store.Disk.TotalBytes / 1048576} MB in {off.Store.Disk.Dir}");
                // colour range: manual range, then another frame -> back to auto
                off.AutoRange = false; off.VminText = "-1"; off.VmaxText = "1"; await Idle(); if (off.Files.Count > 1) { off.SelectedFile = off.Files.First(f => f != off.SelectedFile); await off.ShowTask; await Idle(); }
                log.Add($"REGRESS colour range after frame change: auto={off.AutoRange}, vmin={off.VminText}, vmax={off.VmaxText}");
                // missing result (cache wiped): automatic recompute with progress, and the other tabs follow
                off.Store.Clear(); var first = off.Files[0]; off.SelectedFile = null; var sw0 = System.Diagnostics.Stopwatch.StartNew(); off.SelectedFile = first; await Task.Delay(300); await Idle();
                log.Add($"REGRESS during recompute: IsRecomputing={off.IsRecomputing}, status bar='{vm.StatusMessage}', zernike text='{vm.ZernikeVm.StatsText.Replace("\n", " ")}'");
                await off.ShowTask; await Idle(); await Task.Delay(1500); await Idle();
                log.Add($"REGRESS after recompute ({sw0.Elapsed.TotalSeconds:F1} s): Spec shown = {off.Spec != null}, IsRecomputing={off.IsRecomputing}, zernike rows = {vm.ZernikeVm.Rows.Count}, 3D mesh = {(vm.Surface3D.Mesh != null)}");
                var pp = System.Diagnostics.Process.GetCurrentProcess(); pp.Refresh(); log.Add($"MEM peak working set of the whole process so far: {pp.PeakWorkingSet64 / 1048576} MB (peak private {pp.PeakPagedMemorySize64 / 1048576} MB)");
                off.SelectedFile = off.Files.Last(); await off.ShowTask; await Idle();
            }
            {   // 1.1.0: Zernike all-frames table + Video tab through the real view models
                var za = vm.ZernikeAll; var vid = vm.Video; var off = vm.Offline; var peak = () => { var pp = System.Diagnostics.Process.GetCurrentProcess(); pp.Refresh(); return pp.PeakWorkingSet64 / 1048576; };
                vm.SelectedTab = 1; await Idle(); vm.ZernikeVm.SelectedPreset = "+ defocus"; await Task.Delay(1500); await Idle();
                var sw1 = System.Diagnostics.Stopwatch.StartNew(); await za.ApplyAllCommand.ExecuteAsync(null); await Idle(); await Task.Delay(500); await Idle();
                log.Add($"ZALL per-frame table: {za.Rows.Count(r => !r.IsSummary)} frame rows + {za.Rows.Count(r => r.IsSummary)} summary rows, {za.ColumnDefs.Count} numeric columns, grid columns {win.AllGrid.Columns.Count}, {sw1.Elapsed.TotalSeconds:F1} s; {za.Message}");
                if (za.Table != null && za.Rows.Count > 0) { var r0 = za.Table.Rows[0]; log.Add($"ZALL first row {r0.Name}: power {r0.PowerD:F4} D, astig {r0.AstigRmsNm:F1} nm @ {r0.AstigAxisDeg:F1}°, rms {r0.RmsBeforeNm:F1} -> {r0.RmsAfterNm:F1} nm"); }
                za.SortBy("rms_after_nm"); log.Add("ZALL sorted by rms_after_nm ascending: " + string.Join(", ", za.Rows.Select(r => r.Name))); za.SortBy("rms_after_nm"); log.Add("ZALL descending: " + string.Join(", ", za.Rows.Select(r => r.Name)));
                za.SelectFrameNumber(Math.Min(2, za.Items.Count)); await Task.Delay(2500); await Idle();
                log.Add($"ZALL clicked frame: maps original={(za.OriginalSpec != null)}, removed={(za.RemovedSpec != null)}, filtered={(za.FilteredSpec != null)}; selected file = {off.SelectedFile?.Name}; marker {za.MarkerFrame}; trend series {za.TrendSeries?.Count}; caption: {za.MapsCaption}");
                win.ZernikeSubTabs.SelectedIndex = 1; await Idle(); await Task.Delay(300); await Idle(); Shot("20_zernike_all_frames");
                za.CommonCircle = true; var sw2 = System.Diagnostics.Stopwatch.StartNew(); await za.ApplyAllCommand.ExecuteAsync(null); await Idle();
                log.Add($"ZALL common circle: {za.CircleInfo} ({sw2.Elapsed.TotalSeconds:F1} s); rows with the same circle: {(za.Table != null && za.Table.Rows.Select(r => (r.CircleCx, r.CircleCy, r.CircleRadiusPx)).Distinct().Count() == 1)}");
                off.ExportFolder = Path.Combine(outDir, "zernike_all"); var sw3 = System.Diagnostics.Stopwatch.StartNew(); await za.ExportAllFilteredCommand.ExecuteAsync(null); await Idle();
                log.Add($"ZALL export all filtered: {za.Message} ({sw3.Elapsed.TotalSeconds:F1} s), files in folder: {(Directory.Exists(off.ExportFolder) ? Directory.GetFiles(off.ExportFolder).Length : 0)}"); ZernikeTableCsv.Write(Path.Combine(outDir, "zernike_table_selftest.csv"), za.Table!);
                za.CommonCircle = false; await za.ApplyAllCommand.ExecuteAsync(null); await Idle(); win.ZernikeSubTabs.SelectedIndex = 0;

                vm.SelectedTab = 3; await Idle(); vid.RefreshFrames(); await Idle();
                log.Add($"VIDEO frames: {vid.FramesText}; ffmpeg: {vid.FfmpegStatus.Replace("\n", " | ")}"); off.ExportFolder = Path.Combine(outDir, "video");
                vid.Fps = 10; vid.StripPlot = true; vid.StripValue = vid.StripValues[0]; vid.Panel2 = vid.OptionalPanelChoices[2]; vid.Panel3 = vid.OptionalPanelChoices[4]; vid.Panel4 = vid.OptionalPanelChoices[7];   // filtered OPD, transmittance... see list order
                vid.OutputPath = Path.Combine(outDir, "video", "sequence_4panels_1080p.mp4"); long m0 = peak();
                await vid.RenderCommand.ExecuteAsync(null); await Idle(); log.Add($"VIDEO 4 panels + strip plot, MP4 1080p: {vid.LastRenderSeconds:F1} s; {vid.Message.Replace("\n", " | ")}");
                await vid.PreviewCommand.ExecuteAsync(null); await Idle(); await Task.Delay(500); await Idle(); log.Add($"VIDEO preview: {vid.PreviewInfo}; estimate: {vid.Estimate}"); Shot("21_video_tab");
                vid.Panel2 = vid.OptionalPanelChoices[0]; vid.Panel3 = vid.OptionalPanelChoices[0]; vid.Panel4 = vid.OptionalPanelChoices[0]; vid.StripPlot = false; vid.OutputPath = Path.Combine(outDir, "video", "sequence_opd_1080p.mp4");
                await vid.RenderCommand.ExecuteAsync(null); await Idle(); log.Add($"VIDEO 1 panel OPD, MP4 1080p: {vid.LastRenderSeconds:F1} s; {vid.Message.Replace("\n", " | ")}");
                vid.Format = vid.Formats[1]; vid.OutputPath = Path.Combine(outDir, "video", "sequence_opd.gif"); vid.Resolution = vid.Resolutions[0];
                await vid.RenderCommand.ExecuteAsync(null); await Idle(); log.Add($"VIDEO 1 panel OPD, GIF 720p: {vid.LastRenderSeconds:F1} s; {vid.Message.Replace("\n", " | ")}");
                vid.Format = vid.Formats[0]; vid.Panel1 = vid.PanelChoices[6]; vid.Turntable = true; vid.TurntableSeconds = 3; vid.Fps = 20; vid.OutputPath = Path.Combine(outDir, "video", "turntable.mp4");
                await vid.RenderCommand.ExecuteAsync(null); await Idle(); log.Add($"VIDEO turntable 3 s at 20 fps (60 frames), MP4 720p: {vid.LastRenderSeconds:F1} s; {vid.Message.Replace("\n", " | ")}");
                vid.Turntable = false; vid.Panel1 = vid.PanelChoices[0]; vid.Fps = 10; vid.Format = vid.Formats[3]; vid.OutputPath = Path.Combine(outDir, "video", "png_frames");
                await vid.RenderCommand.ExecuteAsync(null); await Idle(); log.Add($"VIDEO PNG sequence 720p: {vid.LastRenderSeconds:F1} s; {vid.Message.Replace("\n", " | ")}; files {(Directory.Exists(vid.LastOutputPath) ? Directory.GetFiles(vid.LastOutputPath, "*.png").Length : 0)}");
                log.Add($"MEM 1.1.0 block: peak working set of the process {peak()} MB (before the video block {m0} MB)");
                vm.SelectedTab = 0; await Idle(); off.SelectedFile = off.Files.Last(); await off.ShowTask; await Idle();
            }
            vm.Offline.Kind = DisplayKind.Intensity; await Idle(); Shot("03_offline_intensity");
            vm.Offline.Kind = DisplayKind.Phase; await Idle(); Shot("03b_offline_phase");
            vm.Offline.Kind = DisplayKind.OPD;
            if (refFile != "")
            {
                vm.Offline.ReferencePath = refFile; vm.Offline.WithReference = true; await vm.Offline.ProcessAllCommand.ExecuteAsync(null); await Idle(); await Task.Delay(300); await Idle();
                log.Add("with reference: " + vm.Offline.StatsText.Replace("\n", " | ")); Shot("04_offline_with_reference");
            }
            vm.Settings.Export.Matrix.Header = false; vm.Offline.ExportFolder = Path.Combine(outDir, "export"); vm.Settings.Export.Intensity = true; vm.Settings.Export.XyzTxt = true; vm.Settings.Export.Npy = true; vm.Settings.Export.Mat = true; vm.Settings.Export.Tiff32 = true; vm.Settings.Export.Png = true;
            await vm.Offline.ExportCurrentCommand.ExecuteAsync(null); await Idle(); log.Add("export: " + vm.Offline.ExportMessage.Replace("\n", " | ")); Shot("05_offline_exported");
            log.Add(Mem("before zernike tab"));
            // ---- Zernike tab
            vm.Offline.ReferencePath = null; vm.Offline.WithReference = false; vm.Offline.SelectedFile = vm.Offline.Files.FirstOrDefault(); await vm.Offline.ProcessSelectedCommand.ExecuteAsync(null); await Idle();
            vm.SelectedTab = 1; await Task.Delay(1500); await Idle(); Shot("10_zernike_piston_tilt");
            log.Add("zernike default: " + vm.ZernikeVm.StatsText.Replace("\n", " | "));
            vm.ZernikeVm.SelectedPreset = "+ astigmatism"; await Task.Delay(1500); await Idle(); Shot("11_zernike_defocus_astig_removed");
            log.Add("zernike +astig: " + vm.ZernikeVm.StatsText.Replace("\n", " | ") + " || WARN: " + vm.ZernikeVm.WarningText.Replace("\n", " | "));
            log.Add(Mem("after zernike tab"));
            {   // Zernike-tab export buttons
                string zdir = Path.Combine(outDir, "zernike_export"); vm.Offline.ExportFolder = zdir; vm.Settings.Export.Matrix.Header = true; vm.Settings.Export.Npy = true; vm.Settings.Export.Png = false;
                vm.ZernikeVm.SelectedPreset = "+ defocus"; await Task.Delay(1500); await Idle();
                await vm.ZernikeVm.ExportFilteredCommand.ExecuteAsync(null); log.Add("zernike export filtered: " + vm.ZernikeVm.ExportMessage.Replace("\n", " | "));
                await vm.ZernikeVm.ExportRawCommand.ExecuteAsync(null); log.Add("zernike export raw: " + vm.ZernikeVm.ExportMessage.Replace("\n", " | "));
                vm.ZernikeVm.ExportCoefficientsCommand.Execute(null); log.Add("zernike coefficients: " + vm.ZernikeVm.ExportMessage);
                await Idle(); Shot("11b_zernike_export_buttons");
                vm.ZernikeVm.SelectedPreset = "piston + tilt"; await Task.Delay(1200);
            }
            // ---- 3-D tab (hardware D3D11 first, then software WARP)
            vm.ZernikeVm.SelectedPreset = "piston + tilt"; await Task.Delay(1200);
            vm.SelectedTab = 2; await Task.Delay(3000); await Idle(); ShotScreen("12_3d_isometric"); log.Add(Mem("3D iso (hardware)"));
            win.View3D.SavePng(Path.Combine(outDir, "12b_3d_viewport_renderbitmap.png")); log.Add("3D: " + vm.Surface3D.Info.Replace("\n", " | ") + " | software=" + win.View3D.IsSoftware);
            {   // real mouse input on the Direct3D viewport (regression test for the ViewBox hit-test crash): click, left-drag rotate, right-drag pan, wheel, view-cube click
                win.Activate(); var dpi = VisualTreeHelper.GetDpi(win); var o = win.View3D.PointToScreen(new Point(0, 0)); double W = win.View3D.ActualWidth * dpi.DpiScaleX, H = win.View3D.ActualHeight * dpi.DpiScaleY;
                int cx = (int)(o.X + W * 0.45), cy = (int)(o.Y + H * 0.5); string errLog = Path.Combine(Path.GetTempPath(), "qwlsi_error.log"); long before = Qwlsi.Core.AppLog.ErrorCount + Qwlsi.App.Controls.SafeViewport3DX.Swallowed;
                await Click(cx, cy); await Drag(cx, cy, cx + 160, cy - 60, false); await Drag(cx, cy, cx - 80, cy + 50, true);
                mouse_event(WHEEL, 0, 0, 120, UIntPtr.Zero); await Task.Delay(300); mouse_event(WHEEL, 0, 0, -120, UIntPtr.Zero); await Task.Delay(300);
                await Click((int)(o.X + W - 70 * dpi.DpiScaleX), (int)(o.Y + 70 * dpi.DpiScaleY)); await Task.Delay(1200); await Idle(); ShotScreen("12c_3d_after_mouse_interaction");
                long after = Qwlsi.Core.AppLog.ErrorCount + Qwlsi.App.Controls.SafeViewport3DX.Swallowed; log.Add("3D mouse interaction: exception count grew by " + (after - before) + " (0 = no exception)");
            }
            {   // more states a user can reach before clicking: toggle software rendering, leave the tab and come back, change mesh size
                win.Activate(); var dpi = VisualTreeHelper.GetDpi(win); string errLog = Path.Combine(Path.GetTempPath(), "qwlsi_error.log");
                async Task<long> ClickViewport(string tag)
                {
                    long b = Qwlsi.Core.AppLog.ErrorCount + Qwlsi.App.Controls.SafeViewport3DX.Swallowed; var o = win.View3D.PointToScreen(new Point(0, 0)); double W = win.View3D.ActualWidth * dpi.DpiScaleX, H = win.View3D.ActualHeight * dpi.DpiScaleY;
                    await Click((int)(o.X + W * 0.5), (int)(o.Y + H * 0.5)); await Drag((int)(o.X + W * 0.5), (int)(o.Y + H * 0.5), (int)(o.X + W * 0.6), (int)(o.Y + H * 0.45), false);
                    long a = Qwlsi.Core.AppLog.ErrorCount + Qwlsi.App.Controls.SafeViewport3DX.Swallowed; log.Add($"3D mouse [{tag}]: exception count grew by {a - b}; swallowed so far {Qwlsi.App.Controls.SafeViewport3DX.Swallowed}"); return a - b;
                }
                vm.Surface3D.Software = true; await Task.Delay(3000); await Idle(); await ClickViewport("after switching to WARP");
                vm.Surface3D.Software = false; await Task.Delay(3000); await Idle(); await ClickViewport("after switching back to hardware");
                vm.SelectedTab = 0; await Task.Delay(800); vm.SelectedTab = 2; await Task.Delay(2500); await Idle(); await ClickViewport("after leaving the tab and returning");
                vm.Surface3D.Resolution = Qwlsi.Core.MeshResolution.N256; await Task.Delay(2000); await Idle(); await ClickViewport("after changing mesh size");
                vm.Surface3D.Source = Qwlsi.App.ViewModels.SurfaceSource.ZernikeFiltered; await Task.Delay(2500); await Idle(); await ClickViewport("after switching to Zernike-filtered map");
                vm.Surface3D.Source = Qwlsi.App.ViewModels.SurfaceSource.Raw; vm.Surface3D.Resolution = Qwlsi.Core.MeshResolution.N512; await Task.Delay(2000);
            }
            vm.Surface3D.SetViewCommand.Execute("Top"); await Task.Delay(1500); await Idle(); ShotScreen("13_3d_top");
            vm.Surface3D.SetViewCommand.Execute("Isometric"); vm.Surface3D.Wireframe = true; vm.Surface3D.ExaggerationLog = 3.7; await Task.Delay(1800); await Idle(); ShotScreen("14_3d_wireframe_exaggerated");
            vm.Surface3D.Wireframe = false; vm.Surface3D.Resolution = Qwlsi.Core.MeshResolution.N256; vm.Surface3D.Software = true; await Task.Delay(4000); await Idle(); ShotScreen("15_3d_software_warp_256"); log.Add(Mem("3D software WARP"));
            log.Add("3D software: " + vm.Surface3D.Info.Replace("\n", " | ") + " | software=" + win.View3D.IsSoftware);
            vm.Surface3D.Software = false; vm.Surface3D.Resolution = Qwlsi.Core.MeshResolution.N512; await Task.Delay(1500);
            vm.SelectedTab = 4; await Idle();
            if (flat != "") { vm.CalibrationVm.FlatFiles.Add(flat); await vm.CalibrationVm.CalibrateCommand.ExecuteAsync(null); await Idle(); }
            vm.CalibrationVm.AutoFindCommand.Execute(null); await Idle(); Shot("06_calibration");
            vm.SelectedTab = 5; await Idle(); Shot("07_settings");
            {   // About / diagnostics window
                var about = new AboutWindow(vm) { Owner = win, Width = 1000, Height = 760 }; about.Show(); await Task.Delay(1200); await Idle();
                File.WriteAllText(Path.Combine(outDir, "about_diagnostics.txt"), about.ReportText);
                var fe = (FrameworkElement)about.Content; var rtb2 = new RenderTargetBitmap((int)fe.ActualWidth, (int)fe.ActualHeight, 96, 96, PixelFormats.Pbgra32); rtb2.Render(fe);
                var enc2 = new PngBitmapEncoder(); enc2.Frames.Add(BitmapFrame.Create(rtb2)); using (var fs2 = File.Create(Path.Combine(outDir, "09_about.png"))) enc2.Save(fs2); about.Close();
            }
            {   // crash reporter: synthetic exception through the real code path
                var crashPath = CrashReporter.Write(new InvalidOperationException("synthetic self-test exception"), "self-test", vm); log.Add("crash report written: " + crashPath + " (" + (crashPath != null && File.Exists(crashPath) ? new FileInfo(crashPath).Length + " bytes" : "MISSING") + ")");
            }
            vm.SelectedTab = 6; await Idle(); Shot("08_help");
            log.Add("peak memory last frame: " + vm.Offline.LastPeakMb.ToString("F0") + " MB");
            { var pk = System.Diagnostics.Process.GetCurrentProcess(); pk.Refresh(); log.Add($"MEM PEAK of the whole app session: working set {pk.PeakWorkingSet64 / 1048576} MB, pagefile/private {pk.PeakPagedMemorySize64 / 1048576} MB"); log.Add("cache folder at end: " + vm.Offline.Store.Disk.Dir); }
            var pr = System.Diagnostics.Process.GetCurrentProcess(); pr.Refresh(); string M(long b) => (b / 1048576).ToString();
            log.Add($"idle before GC: WS {M(pr.WorkingSet64)} MB, private {M(pr.PrivateMemorySize64)} MB, managed {M(GC.GetTotalMemory(false))} MB");
            Qwlsi.Core.Processor.TrimProcessMemory(); await Idle(); pr.Refresh();
            log.Add($"idle after trim: WS {M(pr.WorkingSet64)} MB, private {M(pr.PrivateMemorySize64)} MB, managed {M(GC.GetTotalMemory(false))} MB");
        }
        catch (Exception e) { log.Add("SELFTEST FAILED: " + e); }
        File.WriteAllLines(Path.Combine(outDir, "selftest.log"), log); Application.Current.Shutdown(); await Task.Delay(1500); Environment.Exit(0);
    }
}
