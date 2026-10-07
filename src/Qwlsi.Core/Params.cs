using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qwlsi.Core;

/// <summary>System parameters (defaults = HANDOFF §3 / PAR in qwlsi_single_frame.py).</summary>
public sealed class QwlsiParams
{
    public double Gamma { get; set; } = 16.44e-6;   // cross-grating period [m]
    public double D { get; set; } = 0.11e-3;        // grating-camera distance [m]
    public double P { get; set; } = 2.74e-6;        // pixel [m]
    public double Z { get; set; } = 1.0;            // relay zoom
    public double Lambda { get; set; } = 650e-9;    // [m]
    public int R { get; set; } = 695;               // spectral window radius [FFT bins of the decimated grid]
    public int N { get; set; } = 4512;              // sensor size [px]
    public int Dec { get; set; } = 3;               // decimation
    public int M => N / Dec;
    public double PitchM => Dec * P / Z;
}

/// <summary>calibration.json — same schema as the Python/MATLAB versions (fa, fb [cyc/px], source, note).</summary>
public sealed class Calibration
{
    [JsonPropertyName("fa")] public double[] Fa { get; set; } = new double[2];
    [JsonPropertyName("fb")] public double[] Fb { get; set; } = new double[2];
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }

    public static Calibration Load(string path) =>
        JsonSerializer.Deserialize<Calibration>(File.ReadAllText(path)) ?? throw new InvalidDataException("empty calibration");

    public void Save(string path) => File.WriteAllText(path,
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
