using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Qwlsi.Core;

namespace Qwlsi.App.ViewModels;

/// <summary>Shell: settings, current calibration (shown in the status bar), shared busy flag, memory read-out.</summary>
public partial class MainViewModel : ObservableObject
{
    public AppSettings Settings { get; }
    public OfflineViewModel Offline { get; }
    public CalibrationViewModel CalibrationVm { get; }
    public SettingsViewModel SettingsVm { get; }

    [ObservableProperty] Calibration? current;
    [ObservableProperty] string? currentPath;
    [ObservableProperty] string calibrationText = "";
    [ObservableProperty] string statusMessage = "Ready.";
    [ObservableProperty] string memoryText = "";
    [ObservableProperty] bool isBusy;
    [ObservableProperty] int selectedTab;

    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainViewModel(AppSettings? settings = null)
    {
        Settings = settings ?? AppSettings.Load();
        Offline = new OfflineViewModel(this); CalibrationVm = new CalibrationViewModel(this); SettingsVm = new SettingsViewModel(this);
        AutoFindCalibration(null, silent: true); UpdateCalibrationText();
        timer.Tick += (_, _) => { var p = Process.GetCurrentProcess(); MemoryText = $"Memory: {p.WorkingSet64 / 1048576} MB working set / {p.PrivateMemorySize64 / 1048576} MB private" + (Offline.LastPeakMb > 0 ? $"  (peak during last frame: {Offline.LastPeakMb:F0} MB)" : ""); };
        timer.Start();
    }

    public QwlsiParams Par => Settings.Params;

    public void SetCalibration(Calibration c, string? path)
    {
        Current = c; CurrentPath = path; if (path != null) Settings.LastCalibrationPath = path; Settings.Save(); UpdateCalibrationText();
    }

    public void UpdateCalibrationText()
    {
        var inv = CultureInfo.InvariantCulture;
        CalibrationText = Current == null ? "Calibration: none loaded (Calibration tab: browse / auto-find / make from a flat frame)"
            : $"Calibration: {(CurrentPath ?? "(unsaved)")}   fa = [{Current.Fa[0].ToString("F6", inv)}, {Current.Fa[1].ToString("F6", inv)}]   fb = [{Current.Fb[0].ToString("F6", inv)}, {Current.Fb[1].ToString("F6", inv)}] cyc/px";
    }

    /// <summary>Order: folder of the loaded image -> app folder -> last-used path -> %APPDATA%\QwlsiApp.</summary>
    public bool AutoFindCalibration(string? imageFolder, bool silent, List<string>? searched = null)
    {
        var path = CalibrationLocator.Find(imageFolder, AppContext.BaseDirectory, Settings.LastCalibrationPath, AppSettings.Dir, searched);
        if (path == null) { if (!silent) StatusMessage = "No calibration.json found in the search locations."; return false; }
        try { SetCalibration(Qwlsi.Core.Calibration.Load(path), path); if (!silent) StatusMessage = "Loaded calibration: " + path; return true; }
        catch (Exception e) { if (!silent) StatusMessage = "Could not read " + path + ": " + e.Message; return false; }
    }
}
