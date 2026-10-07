using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Qwlsi.App.Controls;
using Qwlsi.Core;

namespace Qwlsi.App.ViewModels;

/// <summary>One Zernike term in the checkbox tree.</summary>
public partial class ZNode : ObservableObject
{
    public ZTerm Term { get; } public ZGroup Parent { get; }
    [ObservableProperty] bool isChecked;
    [ObservableProperty] string label = "";
    public ZNode(ZTerm t, ZGroup parent) { Term = t; Parent = parent; Refresh(null); }
    partial void OnIsCheckedChanged(bool value) { Parent.ChildChanged(this); }
    public void Refresh(double? coeffM) => Label = $"j={Term.J,2}  Noll {Term.Noll,2}  (n={Term.N},m={Term.M,2})  {Term.Name}" + (coeffM.HasValue ? $"   {coeffM.Value * 1e9,10:F2} nm" : "");
}

/// <summary>Radial order group (tri-state).</summary>
public partial class ZGroup : ObservableObject
{
    public int Order { get; } public ObservableCollection<ZNode> Children { get; } = new();
    readonly Action<bool> changed; bool busy;
    [ObservableProperty] bool? isChecked = false;
    [ObservableProperty] bool isExpanded;
    public string Header => Order == 0 ? "order 0 — piston" : Order == 1 ? "order 1 — tilt" : Order == 2 ? "order 2 — defocus, astigmatism" : $"order {Order}";
    public ZGroup(int order, Action<bool> onUserChange) { Order = order; changed = onUserChange; IsExpanded = order <= 2; }
    partial void OnIsCheckedChanged(bool? value)
    {
        if (busy || value == null) return; busy = true; foreach (var c in Children) c.IsChecked = value.Value; busy = false; changed(true);
    }
    public void ChildChanged(ZNode _)
    {
        if (busy) return; busy = true; int k = Children.Count(c => c.IsChecked); IsChecked = k == 0 ? false : k == Children.Count ? true : (bool?)null; busy = false; changed(true);
    }
    public void Sync() { busy = true; int k = Children.Count(c => c.IsChecked); IsChecked = k == 0 ? false : k == Children.Count ? true : (bool?)null; busy = false; }
}

public sealed class ZRow
{
    public int J { get; init; } public int Noll { get; init; } public int N { get; init; } public int M { get; init; } public string Name { get; init; } = "";
    public string Nm { get; init; } = ""; public string Diopters { get; init; } = ""; public string Removed { get; init; } = "";
}

public enum ZView { Filtered, Raw, Removed }

public partial class ZernikeViewModel : ObservableObject
{
    readonly MainViewModel main; bool applying; int version;
    public ObservableCollection<ZGroup> Groups { get; } = new();
    public ObservableCollection<ZRow> Rows { get; } = new();
    public IReadOnlyList<string> Presets { get; } = new[] { "piston + tilt", "+ defocus", "+ astigmatism", "up to order N", "custom", "none" };
    public IEnumerable<int> Orders { get; } = Enumerable.Range(0, 11);
    public IEnumerable<ZView> Views { get; } = Enum.GetValues<ZView>();

    [ObservableProperty] string selectedPreset = "piston + tilt";
    [ObservableProperty] int upToOrder = 4;
    [ObservableProperty] ZView view = ZView.Filtered;
    [ObservableProperty] MapSpec? spec;
    [ObservableProperty] string statsText = "No result loaded — process a frame on the Offline tab.";
    [ObservableProperty] string warningText = ""; [ObservableProperty] bool hasWarning;
    [ObservableProperty] string fitText = "";
    [ObservableProperty] bool flipX; [ObservableProperty] bool flipY;

    ReconOutcome? source; ZernikeFit? fit; ZernikeRemoval? removal;
    public ZernikeRemoval? Current => removal;
    /// <summary>Raised on the UI thread when the filtered map changed (new result, new selection, new orientation).</summary>
    public event Action? RemovalChanged;
    public ZernikeOrientation Orientation => main.Settings.ZernikeOrientation;

    public ZernikeViewModel(MainViewModel m)
    {
        main = m; FlipX = Orientation.FlipX; FlipY = Orientation.FlipY;
        foreach (var g in Enumerable.Range(0, 11).Select(n => new ZGroup(n, _ => OnUserTick())))
        {
            foreach (var t in ZernikeBasis.Terms(10).Where(t => t.N == g.Order)) g.Children.Add(new ZNode(t, g));
            Groups.Add(g);
        }
        var saved = m.Settings.ZernikeRemovedJ; ApplySelection(Enumerable.Range(0, 66).Select(j => saved.Contains(j)).ToArray(), false);
        m.Offline.ResultShown += oc => { source = oc; _ = RefitAsync(); };
    }

    public bool[] Selection() { var f = new bool[66]; foreach (var g in Groups) foreach (var c in g.Children) f[c.Term.J] = c.IsChecked; return f; }

    void ApplySelection(bool[] sel, bool recompute = true)
    {
        applying = true; foreach (var g in Groups) { foreach (var c in g.Children) c.IsChecked = sel[c.Term.J]; g.Sync(); } applying = false;
        if (recompute) _ = RecomputeAsync();
    }

    void OnUserTick() { if (applying) return; if (SelectedPreset != "custom") { applying = true; SelectedPreset = "custom"; applying = false; } Persist(); _ = RecomputeAsync(); }

    partial void OnSelectedPresetChanged(string value)
    {
        if (applying || value == "custom") return;
        ApplySelection(value switch { "piston + tilt" => ZernikeTools.Preset("piston+tilt"), "+ defocus" => ZernikeTools.Preset("+defocus"), "+ astigmatism" => ZernikeTools.Preset("+astigmatism"), "up to order N" => ZernikeTools.Preset("order", UpToOrder), _ => ZernikeTools.Preset("none") });
        Persist();
    }
    partial void OnUpToOrderChanged(int value) { if (SelectedPreset == "up to order N") { ApplySelection(ZernikeTools.Preset("order", value)); Persist(); } }
    partial void OnViewChanged(ZView value) { RefreshMap(); }
    partial void OnFlipXChanged(bool value) { Orientation.FlipX = value; main.Settings.Save(); _ = RefitAsync(); }
    partial void OnFlipYChanged(bool value) { Orientation.FlipY = value; main.Settings.Save(); _ = RefitAsync(); }

    void Persist() { var s = Selection(); main.Settings.ZernikeRemovedJ = Enumerable.Range(0, 66).Where(j => s[j]).ToArray(); main.Settings.Save(); }

    async Task RefitAsync()
    {
        int v = ++version; var oc = source;
        if (oc == null) { fit = null; removal = null; Spec = null; Rows.Clear(); StatsText = "No result loaded — process a frame on the Offline tab."; WarningText = ""; HasWarning = false; return; }
        var o = new ZernikeOrientation { FlipX = Orientation.FlipX, FlipY = Orientation.FlipY }; var par = main.Settings.Params; var sel = Selection();
        var (f, zr) = await Task.Run(() => { var ff = ZernikeTools.Fit(oc.Result.Opd, oc.Result.Mask, oc.Result.M, par.PitchM, 10, o); return (ff, ZernikeRemoval.Make(oc.Result.Opd, oc.Result.Mask, ff, sel)); });
        if (v != version) return; fit = f; removal = zr; UpdateTable(); UpdateTexts(); RefreshMap(); RemovalChanged?.Invoke();
    }

    async Task RecomputeAsync()
    {
        if (fit == null || source == null) return; int v = ++version; var f = fit; var oc = source; var sel = Selection();
        var zr = await Task.Run(() => ZernikeRemoval.Make(oc.Result.Opd, oc.Result.Mask, f, sel));
        if (v != version) return; removal = zr; UpdateTable(); UpdateTexts(); RefreshMap(); RemovalChanged?.Invoke();
    }

    void UpdateTable()
    {
        if (fit == null || removal == null) return; var inv = CultureInfo.InvariantCulture; Rows.Clear();
        foreach (var t in fit.Terms)
        {
            double d = fit.Diopters(t.J);
            Rows.Add(new ZRow { J = t.J, Noll = t.Noll, N = t.N, M = t.M, Name = t.Name, Nm = (fit.Coeff[t.J] * 1e9).ToString("F2", inv), Diopters = double.IsNaN(d) ? "—" : d.ToString("+0.000;-0.000", inv), Removed = removal.Removed[t.J] ? "✓ removed" : "" });
            foreach (var g in Groups) foreach (var c in g.Children) if (c.Term.J == t.J) c.Refresh(fit.Coeff[t.J]);
        }
    }

    void UpdateTexts()
    {
        if (fit == null || removal == null) return; var inv = CultureInfo.InvariantCulture; var zr = removal;
        int nRem = zr.Removed.Count(b => b);
        FitText = $"Joint masked least-squares fit, {fit.Terms.Length} terms (order 10), {fit.FitPixels:N0} px. Circle: centre ({fit.Cx:F1}, {fit.Cy:F1}) px, radius {fit.RadiusPx:F1} px = {fit.RadiusM * 1e3:F3} mm.\nConvention: {fit.Convention}.";
        StatsText = $"{nRem} term(s) removed\n" +
                    $"RMS   before {zr.RmsBeforeM * 1e9,9:F1} nm   after {zr.RmsAfterM * 1e9,9:F1} nm\n" +
                    $"PV    before {zr.PvBeforeM * 1e6,9:F3} µm   after {zr.PvAfterM * 1e6,9:F3} µm" +
                    (zr.Removed[4] ? $"\nremoved defocus: {fit.Diopters(4).ToString("+0.000;-0.000", inv)} D" : "") +
                    (zr.Removed[3] || zr.Removed[5] ? $"\nremoved astigmatism (power vector): J45 {fit.Diopters(3).ToString("+0.000;-0.000", inv)} D, J0 {fit.Diopters(5).ToString("+0.000;-0.000", inv)} D" : "");
        var w = ZernikeTools.Warnings(zr.Removed); WarningText = string.Join("\n", w); HasWarning = w.Count > 0;
    }

    void RefreshMap()
    {
        if (source == null || removal == null) return; var raw = source.Result.Opd; var filt = removal.Filtered; var d = new float[raw.Length];
        for (int i = 0; i < d.Length; i++) d[i] = View == ZView.Raw ? raw[i] * 1e9f : View == ZView.Filtered ? filt[i] * 1e9f : (raw[i] - filt[i]) * 1e9f;
        var (lo, hi) = Colormap.AutoRange(d);
        Spec = new MapSpec { Data = d, Width = source.Result.M, Height = source.Result.M, Cmap = main.Offline.Cmap, VMin = lo, VMax = hi, Unit = View == ZView.Removed ? "nm (removed surface)" : View == ZView.Raw ? "nm (raw OPD)" : "nm (filtered OPD)", Title = Path.GetFileName(source.File) };
    }

    /// <summary>For exports: the removal of the CURRENT selection for any outcome (fit order 10, current orientation). Thread-safe; may be slow (~0.3 s).</summary>
    public ZernikeRemoval MakeFor(ReconOutcome oc, bool[] selection, QwlsiParams par)
    {
        var o = new ZernikeOrientation { FlipX = Orientation.FlipX, FlipY = Orientation.FlipY };
        var f = ZernikeTools.Fit(oc.Result.Opd, oc.Result.Mask, oc.Result.M, par.PitchM, 10, o); return ZernikeRemoval.Make(oc.Result.Opd, oc.Result.Mask, f, selection);
    }

    [RelayCommand] void ExpandAll() { foreach (var g in Groups) g.IsExpanded = true; }
    [RelayCommand] void CollapseAll() { foreach (var g in Groups) g.IsExpanded = false; }
}
