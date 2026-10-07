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
    public App()
    {
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
    public static async Task Run(MainWindow win, MainViewModel vm, string outDir, string file, string refFile, string calib, string flat)
    {
        Directory.CreateDirectory(outDir); var log = new List<string>(); File.WriteAllText(Path.Combine(outDir, "selftest.started"), DateTime.Now.ToString());
        void Shot(string name)
        {
            win.UpdateLayout(); var fe = (FrameworkElement)win.Content; int w = (int)fe.ActualWidth, h = (int)fe.ActualHeight;
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); rtb.Render(fe);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb)); using var fs = File.Create(Path.Combine(outDir, name + ".png")); enc.Save(fs);
        }
        Task Idle() => win.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
        try
        {
            win.Width = 1500; win.Height = 920; await Idle();
            if (calib != "") vm.SetCalibration(Qwlsi.Core.Calibration.Load(calib), calib);
            vm.SelectedTab = 0; await Idle(); Shot("01_offline_empty");
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
            vm.SelectedTab = 1; await Idle();
            if (flat != "") { vm.CalibrationVm.FlatFiles.Add(flat); await vm.CalibrationVm.CalibrateCommand.ExecuteAsync(null); await Idle(); }
            vm.CalibrationVm.AutoFindCommand.Execute(null); await Idle(); Shot("06_calibration");
            vm.SelectedTab = 2; await Idle(); Shot("07_settings");
            vm.SelectedTab = 3; await Idle(); Shot("08_help");
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
