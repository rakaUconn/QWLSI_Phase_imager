using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Qwlsi.Core;

namespace Qwlsi.App.ViewModels;

public partial class CalibrationViewModel : ObservableObject
{
    readonly MainViewModel main;
    [ObservableProperty] string newCarriers = "(no new calibration yet)";
    [ObservableProperty] string diagnostics = "";
    [ObservableProperty] string checkResult = "";
    [ObservableProperty] string searchLog = "";
    [ObservableProperty] bool hasCandidate;
    [ObservableProperty] bool averageFrames = true;
    [ObservableProperty] string progressText = "";
    Calibration? candidate;
    public ObservableCollection<string> FlatFiles { get; } = new();
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public CalibrationViewModel(MainViewModel m) { main = m; }

    [RelayCommand]
    void Browse()
    {
        var dlg = new OpenFileDialog { Filter = "calibration.json|*.json|All files|*.*", Title = "Open calibration file", InitialDirectory = Dir(main.CurrentPath) };
        if (dlg.ShowDialog() != true) return;
        try { main.SetCalibration(Qwlsi.Core.Calibration.Load(dlg.FileName), dlg.FileName); main.StatusMessage = "Loaded " + dlg.FileName; SearchLog = ""; }
        catch (Exception e) { main.StatusMessage = "Not a valid calibration file: " + e.Message; }
    }

    [RelayCommand]
    void AutoFind()
    {
        var searched = new List<string>(); string? imgFolder = main.Offline.CurrentImageFolder;
        bool ok = main.AutoFindCalibration(imgFolder, false, searched);
        SearchLog = "Search order (first hit wins):\n" + string.Join("\n", new[] { "1. folder of the loaded image", "2. app folder", "3. last-used path", "4. %APPDATA%\\QwlsiApp" }.Select((t, i) => t + (i < searched.Count ? "  →  " + searched[i] : "  (not set)")))
            + "\n" + (ok ? "FOUND: " + main.CurrentPath : "Not found.");
    }

    [RelayCommand]
    void AddFlatFiles()
    {
        var dlg = new OpenFileDialog { Filter = "TIFF frames|*.tif;*.tiff|All files|*.*", Multiselect = true, Title = "Select flat / collimated frame(s)", InitialDirectory = Dir(main.Settings.LastImageFolder) };
        if (dlg.ShowDialog() != true) return; foreach (var f in dlg.FileNames) FlatFiles.Add(f);
    }

    [RelayCommand] void ClearFlatFiles() { FlatFiles.Clear(); }

    [RelayCommand]
    async Task Calibrate()
    {
        if (FlatFiles.Count == 0) { AddFlatFiles(); if (FlatFiles.Count == 0) return; }
        if (main.IsBusy) return; main.IsBusy = true; HasCandidate = false;
        var files = AverageFrames ? FlatFiles.ToList() : new List<string> { FlatFiles[0] };
        var par = main.Settings.Params; Calibration? c = null; CalibrationDiagnostics? dg = null; string? err = null;
        var prog = new Progress<string>(s => { ProgressText = s; main.StatusMessage = s; });
        await Task.Run(() =>
        {
            try { using var s = new ReconSession { Par = par }; c = s.CalibrateFromFlat(files, prog); dg = s.LastCalibrationDiagnostics; }
            catch (Exception e) { err = e.Message; }
        });
        main.IsBusy = false; ProgressText = ""; Processor.TrimProcessMemory();
        if (err != null || c == null) { main.StatusMessage = "Calibration failed: " + err; AppLog.Error("calibration from flat failed: " + err); return; }
        candidate = c; HasCandidate = true; AppLog.Info($"calibration from flat computed from {files.Count} frame(s): fa=[{c.Fa[0]:R},{c.Fa[1]:R}] fb=[{c.Fb[0]:R},{c.Fb[1]:R}]");
        var cur = main.Current;
        NewCarriers = $"fa = [{c.Fa[0].ToString("F9", Inv)}, {c.Fa[1].ToString("F9", Inv)}]   fb = [{c.Fb[0].ToString("F9", Inv)}, {c.Fb[1].ToString("F9", Inv)}]  cyc/px   (from {files.Count} frame(s))";
        Diagnostics = (dg != null ? $"Residual of the phase-plane fit: {dg.PlaneFitResidualRad[0]:F3} / {dg.PlaneFitResidualRad[1]:F3} rad rms (a / b) — non-planar part of the flat-frame wavefront.\nLast carrier update: {dg.LastUpdateCyclesPerPx[0]:E1} / {dg.LastUpdateCyclesPerPx[1]:E1} cyc/px (converged when ≲1e-8).\n" : "")
            + (cur != null ? $"Difference to the loaded calibration: Δfa = [{c.Fa[0] - cur.Fa[0]:E2}, {c.Fa[1] - cur.Fa[1]:E2}]  Δfb = [{c.Fb[0] - cur.Fb[0]:E2}, {c.Fb[1] - cur.Fb[1]:E2}] cyc/px." : "No calibration loaded to compare with.");
        main.StatusMessage = "New calibration computed — review, then 'Use' and/or 'Save as calibration.json'.";
    }

    [RelayCommand]
    void UseCandidate() { if (candidate == null) return; main.SetCalibration(candidate, null); main.StatusMessage = "New calibration is now active (not yet saved to a file)."; }

    [RelayCommand]
    void SaveCandidate()
    {
        var c = candidate ?? main.Current; if (c == null) return;
        var dlg = new SaveFileDialog { Filter = "calibration.json|*.json", FileName = "calibration.json", Title = "Save calibration", InitialDirectory = Dir(main.CurrentPath ?? main.Settings.LastImageFolder) };
        if (dlg.ShowDialog() != true) return;
        try { c.Save(dlg.FileName); main.SetCalibration(c, dlg.FileName); main.StatusMessage = "Saved " + dlg.FileName; }
        catch (Exception e) { main.StatusMessage = "Save failed: " + e.Message; }
    }

    /// <summary>Process a flat frame with the loaded calibration: lens power should read 0.00 D.</summary>
    [RelayCommand]
    async Task CheckOnFlat()
    {
        if (main.Current == null) { main.StatusMessage = "Load a calibration first."; return; }
        var dlg = new OpenFileDialog { Filter = "TIFF frames|*.tif;*.tiff", Title = "Select a flat frame to check the calibration", InitialDirectory = Dir(main.Settings.LastImageFolder) };
        if (dlg.ShowDialog() != true || main.IsBusy) return; main.IsBusy = true; CheckResult = "processing…";
        var par = main.Settings.Params; var cal = main.Current; string f = dlg.FileName; string? err = null; ReconOutcome? oc = null;
        await Task.Run(() => { try { using var s = new ReconSession { Par = par, Calibration = cal }; oc = s.ProcessFile(f); } catch (Exception e) { err = e.Message; } });
        main.IsBusy = false;
        if (oc == null) { CheckResult = "failed: " + err; return; }
        var st = oc.Result.Stats; double d = Math.Abs(st.PowerD);
        CheckResult = $"{Path.GetFileName(f)}: power {st.PowerD:+0.000;-0.000} D, astigmatism {st.AstigRmsNm:F0} nm, rms {st.RmsNm:F0} nm  →  " + (d <= 0.02 ? "OK (flat reads ≈ 0.00 D)" : "NOT flat: re-make the calibration from this frame (a carrier error is indistinguishable from defocus).");
    }

    static string? Dir(string? p) { try { if (string.IsNullOrEmpty(p)) return null; var d = Directory.Exists(p) ? p : Path.GetDirectoryName(p); return d != null && Directory.Exists(d) ? d : null; } catch { return null; } }
}
