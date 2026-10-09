using System.Text.Json;

namespace Qwlsi.Core;

public enum Units { Nm, Um, M, Waves }
public enum Delimiter { Tab, Space, Comma }
public enum FieldLayout { ReIm, AmpPhase }

public sealed class MatrixOptions
{
    public Delimiter Delim { get; set; } = Delimiter.Tab;
    public int Decimals { get; set; } = 3;
    public Units Unit { get; set; } = Units.Nm;
    /// <summary>Outside-pupil values: false = NaN, true = 0.</summary>
    public bool OutsideAsZero { get; set; }
    /// <summary>Optional '#' comment header (off by default so the file is a pure matrix).</summary>
    public bool Header { get; set; }
}

public sealed class ExportOptions
{
    public bool Opd { get; set; } = true;
    public bool Intensity { get; set; }
    public bool Mask { get; set; }
    public bool Field { get; set; }
    public FieldLayout FieldPair { get; set; } = FieldLayout.ReIm;
    public bool MatrixTxt { get; set; } = true;
    public bool XyzTxt { get; set; }
    public bool XyzDropMasked { get; set; } = true;
    public bool Mat { get; set; }
    public bool Npy { get; set; }
    public bool Tiff32 { get; set; }
    public bool Png { get; set; }
    public MatrixOptions Matrix { get; set; } = new();
}

/// <summary>Persistent app settings (%APPDATA%\QwlsiApp\settings.json).</summary>
public sealed class AppSettings
{
    public QwlsiParams Params { get; set; } = new();
    public string? LastCalibrationPath { get; set; }
    public string? LastImageFolder { get; set; }
    public string? LastExportFolder { get; set; }
    public string? LastReferencePath { get; set; }
    public string? SystemWavefrontPath { get; set; }
    public string Colormap { get; set; } = "Viridis";
    public ExportOptions Export { get; set; } = new();
    public bool AutoExportBatch { get; set; }
    /// <summary>Zernike removal (OSA/ANSI, order 10): orientation flags, ticked terms (ANSI j) and whether exports use the filtered OPD.</summary>
    public ZernikeOrientation ZernikeOrientation { get; set; } = new();
    public int[] ZernikeRemovedJ { get; set; } = new[] { 0, 1, 2 };
    public bool ExportFilteredOpd { get; set; }
    /// <summary>Size limit of the temporary result cache on disk [MB] (see <see cref="CacheDir"/>).</summary>
    public int CacheLimitMb { get; set; } = 2048;

    public static string Dir => Environment.GetEnvironmentVariable("QWLSI_APPDATA") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QwlsiApp");
    /// <summary>Temporary result cache root: %LOCALAPPDATA%\QwlsiApp\cache (inside QWLSI_APPDATA when that is set, e.g. by the self-test).</summary>
    public static string CacheDir => Environment.GetEnvironmentVariable("QWLSI_APPDATA") is { Length: > 0 } d ? Path.Combine(d, "cache") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QwlsiApp", "cache");
    /// <summary>Default export folder: Documents\QwlsiApp\Output.</summary>
    public static string DefaultOutputDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "QwlsiApp", "Output");
    public static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Js = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static AppSettings Load(string? path = null)
    {
        try { var p = path ?? FilePath; if (File.Exists(p)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(p), Js) ?? new(); } catch { }
        return new();
    }

    public void Save(string? path = null)
    {
        var p = path ?? FilePath; Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, JsonSerializer.Serialize(this, Js));
    }
}

/// <summary>calibration.json auto-search (HANDOFF §5): folder of the loaded image -> app folder -> last-used path -> %APPDATA%\QwlsiApp.</summary>
public static class CalibrationLocator
{
    public const string FileName = "calibration.json";

    public static string? Find(string? imageFolder, string? appFolder, string? lastUsed, string? appData, List<string>? searched = null)
    {
        var cands = new List<string>();
        if (!string.IsNullOrEmpty(imageFolder)) cands.Add(Path.Combine(imageFolder, FileName));
        if (!string.IsNullOrEmpty(appFolder)) cands.Add(Path.Combine(appFolder, FileName));
        if (!string.IsNullOrEmpty(lastUsed)) cands.Add(lastUsed);
        if (!string.IsNullOrEmpty(appData)) cands.Add(Path.Combine(appData, FileName));
        foreach (var c in cands) { searched?.Add(c); if (File.Exists(c)) return c; }
        return null;
    }
}
