using System.Diagnostics;
using System.Text;

namespace Qwlsi.Video;

/// <summary>Finds ffmpeg.exe (option, QWLSI_FFMPEG, app folder, PATH), reports its version and probes which H.264 encoder actually works on this machine (cached per path).</summary>
public static class FfmpegLocator
{
    static readonly Dictionary<string, string?> versions = new(StringComparer.OrdinalIgnoreCase), encoders = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>H.264 encoders in order of preference. libx264 only exists in GPL builds; the bundled LGPL build offers the others.</summary>
    static readonly string[] Preference = { "libx264", "h264_mf", "h264_nvenc", "libopenh264", "h264_qsv", "h264_amf" };

    public static string? Find(string? explicitPath = null)
    {
        var cands = new List<string?> { explicitPath, Environment.GetEnvironmentVariable("QWLSI_FFMPEG"), Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"), Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe") };
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) { try { cands.Add(Path.Combine(d.Trim('"'), "ffmpeg.exe")); } catch { } }
        foreach (var c in cands) { try { if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) return Path.GetFullPath(c); } catch { } }
        return null;
    }

    public static bool Found(string? explicitPath = null) => Find(explicitPath) != null;

    /// <summary>First line of `ffmpeg -version` (null when it cannot be run).</summary>
    public static string? VersionString(string path)
    {
        lock (versions) { if (versions.TryGetValue(path, out var v)) return v; }
        var r = Run(path, "-hide_banner -version", 10000); string? line = r.ok ? r.stdout.Split('\n')[0].Trim() : null;
        lock (versions) versions[path] = line; return line;
    }

    /// <summary>Name of the first H.264 encoder of this ffmpeg that really encodes (tiny test encode), or null.</summary>
    public static string? ProbeH264Encoder(string path)
    {
        lock (encoders) { if (encoders.TryGetValue(path, out var v)) return v; }
        string? found = null; var list = Run(path, "-hide_banner -encoders", 15000);
        if (list.ok)
            foreach (var enc in Preference)
            {
                if (!list.stdout.Contains(" " + enc + " ", StringComparison.Ordinal)) continue;
                var t = Run(path, $"-hide_banner -loglevel error -y -f lavfi -i testsrc2=size=256x144:rate=10 -frames:v 3 -pix_fmt yuv420p -c:v {enc} -f null -", 20000);
                if (t.ok) { found = enc; break; }
            }
        lock (encoders) encoders[path] = found; return found;
    }

    public static (bool ok, string stdout, string stderr) Run(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            using var p = Process.Start(psi)!; var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (false, "", "timeout"); }
            p.WaitForExit(); return (p.ExitCode == 0, so.Result, se.Result);
        }
        catch (Exception e) { return (false, "", e.Message); }
    }
}
