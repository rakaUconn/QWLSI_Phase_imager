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
    static void LogError(Exception e)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "qwlsi_error.log"), DateTime.Now + Environment.NewLine + e + Environment.NewLine + Environment.NewLine); } catch { }
        try { if (Current?.MainWindow?.DataContext is MainViewModel vm) vm.StatusMessage = "A 3-D view input error was ignored (details in " + Path.Combine(Path.GetTempPath(), "qwlsi_error.log") + ")."; } catch { }
    }

    public App()
    {
        // The 3-D viewer lives in a WinForms host: its exceptions would otherwise pop up the modal WinForms crash dialog.
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, e) => LogError(e.Exception);
        DispatcherUnhandledException += (_, e) =>
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "qwlsi_error.log"), DateTime.Now + Environment.NewLine + e.Exception + Environment.NewLine + Environment.NewLine); } catch { }
            if (!SelfTestMode) MessageBox.Show(e.Exception.ToString(), "QwlsiApp error"); else Shutdown(2);
            e.Handled = true;
        };
    }

    void OnStartup(object sender, StartupEventArgs e)
    {
        var args = e.Args; string? Opt(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var selftest = Opt("--selftest"); SelfTestMode = selftest != null; if (selftest != null) Directory.CreateDirectory(selftest);
        var settings = selftest != null ? new AppSettings() : null;      // self-test: defaults, never touches the user's settings.json
        var vm = new MainViewModel(settings);
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
                double W0 = win.View3D.ActualWidth * dpi0.DpiScaleX, H0 = win.View3D.ActualHeight * dpi0.DpiScaleY; string el = Path.Combine(Path.GetTempPath(), "qwlsi_error.log"); long b0 = File.Exists(el) ? new FileInfo(el).Length : 0;
                int ex = (int)(o0.X + W0 * 0.5), ey = (int)(o0.Y + H0 * 0.5); await Click(ex, ey); await Drag(ex, ey, ex + 100, ey + 40, false); await Drag(ex, ey, ex - 60, ey - 30, true);
                mouse_event(WHEEL, 0, 0, 120, UIntPtr.Zero); await Task.Delay(300); await Click((int)(o0.X + W0 - 70 * dpi0.DpiScaleX), (int)(o0.Y + 70 * dpi0.DpiScaleY)); await Task.Delay(500);
                long a0 = File.Exists(el) ? new FileInfo(el).Length : 0; log.Add("3D mouse on EMPTY viewport: error log grew by " + (a0 - b0) + " bytes (0 = no exception)"); vm.SelectedTab = 0; await Idle();
            }
            vm.Offline.AddPaths(file.Split(';', StringSplitOptions.RemoveEmptyEntries)); await Idle();
            await vm.Offline.ProcessAllCommand.ExecuteAsync(null); await Idle(); await Task.Delay(300); await Idle();
            log.Add("without reference: " + vm.Offline.StatsText.Replace("\n", " | ")); Shot("02_offline_opd");
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
            // ---- 3-D tab (hardware D3D11 first, then software WARP)
            vm.ZernikeVm.SelectedPreset = "piston + tilt"; await Task.Delay(1200);
            vm.SelectedTab = 2; await Task.Delay(3000); await Idle(); ShotScreen("12_3d_isometric"); log.Add(Mem("3D iso (hardware)"));
            win.View3D.SavePng(Path.Combine(outDir, "12b_3d_viewport_renderbitmap.png")); log.Add("3D: " + vm.Surface3D.Info.Replace("\n", " | ") + " | software=" + win.View3D.IsSoftware);
            {   // real mouse input on the Direct3D viewport (regression test for the ViewBox hit-test crash): click, left-drag rotate, right-drag pan, wheel, view-cube click
                win.Activate(); var dpi = VisualTreeHelper.GetDpi(win); var o = win.View3D.PointToScreen(new Point(0, 0)); double W = win.View3D.ActualWidth * dpi.DpiScaleX, H = win.View3D.ActualHeight * dpi.DpiScaleY;
                int cx = (int)(o.X + W * 0.45), cy = (int)(o.Y + H * 0.5); string errLog = Path.Combine(Path.GetTempPath(), "qwlsi_error.log"); long before = File.Exists(errLog) ? new FileInfo(errLog).Length : 0;
                await Click(cx, cy); await Drag(cx, cy, cx + 160, cy - 60, false); await Drag(cx, cy, cx - 80, cy + 50, true);
                mouse_event(WHEEL, 0, 0, 120, UIntPtr.Zero); await Task.Delay(300); mouse_event(WHEEL, 0, 0, -120, UIntPtr.Zero); await Task.Delay(300);
                await Click((int)(o.X + W - 70 * dpi.DpiScaleX), (int)(o.Y + 70 * dpi.DpiScaleY)); await Task.Delay(1200); await Idle(); ShotScreen("12c_3d_after_mouse_interaction");
                long after = File.Exists(errLog) ? new FileInfo(errLog).Length : 0; log.Add("3D mouse interaction: error log grew by " + (after - before) + " bytes (0 = no exception)");
            }
            vm.Surface3D.SetViewCommand.Execute("Top"); await Task.Delay(1500); await Idle(); ShotScreen("13_3d_top");
            vm.Surface3D.SetViewCommand.Execute("Isometric"); vm.Surface3D.Wireframe = true; vm.Surface3D.ExaggerationLog = 3.7; await Task.Delay(1800); await Idle(); ShotScreen("14_3d_wireframe_exaggerated");
            vm.Surface3D.Wireframe = false; vm.Surface3D.Resolution = Qwlsi.Core.MeshResolution.N256; vm.Surface3D.Software = true; await Task.Delay(4000); await Idle(); ShotScreen("15_3d_software_warp_256"); log.Add(Mem("3D software WARP"));
            log.Add("3D software: " + vm.Surface3D.Info.Replace("\n", " | ") + " | software=" + win.View3D.IsSoftware);
            vm.Surface3D.Software = false; vm.Surface3D.Resolution = Qwlsi.Core.MeshResolution.N512; await Task.Delay(1500);
            vm.SelectedTab = 3; await Idle();
            if (flat != "") { vm.CalibrationVm.FlatFiles.Add(flat); await vm.CalibrationVm.CalibrateCommand.ExecuteAsync(null); await Idle(); }
            vm.CalibrationVm.AutoFindCommand.Execute(null); await Idle(); Shot("06_calibration");
            vm.SelectedTab = 4; await Idle(); Shot("07_settings");
            vm.SelectedTab = 5; await Idle(); Shot("08_help");
            log.Add("peak memory last frame: " + vm.Offline.LastPeakMb.ToString("F0") + " MB");
            var pr = System.Diagnostics.Process.GetCurrentProcess(); pr.Refresh(); string M(long b) => (b / 1048576).ToString();
            log.Add($"idle before GC: WS {M(pr.WorkingSet64)} MB, private {M(pr.PrivateMemorySize64)} MB, managed {M(GC.GetTotalMemory(false))} MB");
            Qwlsi.Core.Processor.TrimProcessMemory(); await Idle(); pr.Refresh();
            log.Add($"idle after trim: WS {M(pr.WorkingSet64)} MB, private {M(pr.PrivateMemorySize64)} MB, managed {M(GC.GetTotalMemory(false))} MB");
        }
        catch (Exception e) { log.Add("SELFTEST FAILED: " + e); }
        File.WriteAllLines(Path.Combine(outDir, "selftest.log"), log); Application.Current.Shutdown(); await Task.Delay(1500); Environment.Exit(0);
    }
}
