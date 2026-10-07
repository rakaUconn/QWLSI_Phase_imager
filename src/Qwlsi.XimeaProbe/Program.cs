using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Phase-0 probe (no camera attached): checks that the xiApi.NETX64 wrapper (and native xiapi64.dll) load,
// and that GetNumberDevices returns without crashing. Does not open any device.
static class Probe
{
    // All xiApi types live in this method so a load failure is catchable in Main (not at JIT time of Main).
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int CountDevices(out string location)
    {
        var cam = new xiApi.NET.xiCam();
        location = typeof(xiApi.NET.xiCam).Assembly.Location;
        int n = 0; cam.GetNumberDevices(out n); return n;
    }

    /// <summary>Pre-loads xiapi64.dll from the app folder or the XIMEA API install so the wrapper's DllImport resolves it.</summary>
    static string? PreloadNative()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, @"C:\XIMEA\API\xiAPI" })
        {
            var f = Path.Combine(dir, "xiapi64.dll");
            if (File.Exists(f) && NativeLibrary.TryLoad(f, out _)) return f;
        }
        return null;
    }

    static int Main()
    {
        try
        {
            Console.WriteLine("native xiapi64.dll: " + (PreloadNative() ?? @"not found in app folder or C:\XIMEA\API\xiAPI (relying on PATH)"));
            int n = CountDevices(out var loc);
            Console.WriteLine($"xiApi.NETX64 loaded from: {loc}");
            Console.WriteLine(n == 0 ? "No devices found" : $"{n} device(s) found (probe does not open them)");
            return 0;
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or FileNotFoundException or FileLoadException or TypeInitializationException)
        {
            Console.WriteLine("xiApi unavailable: " + e.GetType().Name + ": " + e.Message);
            Console.WriteLine("(app must still start in offline/simulator mode)");
            return 2;
        }
        catch (Exception e)
        {
            Console.WriteLine("xiApi call failed: " + e.GetType().Name + ": " + e.Message);
            return 1;
        }
    }
}
