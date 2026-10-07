using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Qwlsi.Core;

namespace Qwlsi.App.ViewModels;

/// <summary>System parameters in lab units (µm, mm, nm). Defaults: HANDOFF §3, λ = 650 nm. Saved in settings.json.</summary>
public partial class SettingsViewModel : ObservableObject
{
    readonly MainViewModel main;
    [ObservableProperty] string gammaUm = "", dMm = "", pUm = "", zoom = "", lambdaNm = "", radius = "", decimation = "", sensor = "";
    [ObservableProperty] string message = "";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public SettingsViewModel(MainViewModel m) { main = m; Load(m.Settings.Params); }

    void Load(QwlsiParams p)
    {
        GammaUm = (p.Gamma * 1e6).ToString("G6", Inv); DMm = (p.D * 1e3).ToString("G6", Inv); PUm = (p.P * 1e6).ToString("G6", Inv); Zoom = p.Z.ToString("G6", Inv);
        LambdaNm = (p.Lambda * 1e9).ToString("G6", Inv); Radius = p.R.ToString(Inv); Decimation = p.Dec.ToString(Inv); Sensor = p.N.ToString(Inv);
    }

    static bool Num(string s, out double v) => double.TryParse(s.Trim(), NumberStyles.Float, Inv, out v) && v > 0 && double.IsFinite(v);

    [RelayCommand]
    void Apply()
    {
        if (!Num(GammaUm, out var g) || !Num(DMm, out var d) || !Num(PUm, out var p) || !Num(Zoom, out var z) || !Num(LambdaNm, out var l) || !Num(Radius, out var r) || !Num(Decimation, out var dec) || !Num(Sensor, out var n))
        { Message = "All parameters must be positive numbers."; return; }
        if (((int)n) % ((int)dec) != 0) { Message = "Sensor size must be divisible by the decimation."; return; }
        var q = main.Settings.Params; q.Gamma = g * 1e-6; q.D = d * 1e-3; q.P = p * 1e-6; q.Z = z; q.Lambda = l * 1e-9; q.R = (int)r; q.Dec = (int)dec; q.N = (int)n;
        main.Settings.Save(); Message = "Saved to " + AppSettings.FilePath + ". Takes effect on the next Process.";
        main.StatusMessage = $"Parameters saved (λ = {l:G6} nm).";
    }

    [RelayCommand]
    void ResetDefaults() { Load(new QwlsiParams()); Message = "Defaults restored (press Save to keep them)."; }
}
