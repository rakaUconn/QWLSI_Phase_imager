using System.Globalization;
using System.Text;
using BitMiracle.LibTiff.Classic;

namespace Qwlsi.Core;

/// <summary>Low-level file writers: text matrix, XYZ list, MAT v5, NPY, float32 TIFF.</summary>
public static class Writers
{
    public static double UnitScale(Units u, double lambda) => u switch { Units.Nm => 1e9, Units.Um => 1e6, Units.M => 1.0, Units.Waves => 1.0 / lambda, _ => 1.0 };
    public static string UnitName(Units u) => u switch { Units.Nm => "nm", Units.Um => "um", Units.M => "m", Units.Waves => "waves", _ => "" };
    public static char DelimChar(Delimiter d) => d switch { Delimiter.Tab => '\t', Delimiter.Space => ' ', _ => ',' };

    /// <summary>Streams a rows x cols matrix, one image row per line. NaN is written as "NaN" or "0" (outsideZero).</summary>
    public static void WriteMatrixText(string path, float[] data, int rows, int cols, double scale, MatrixOptions o, IEnumerable<string>? headerLines = null, bool applyScale = true)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        using var w = new StreamWriter(fs, new ASCIIEncoding(), 1 << 20) { NewLine = "\n" };
        if (o.Header && headerLines != null) foreach (var h in headerLines) w.WriteLine("# " + h);
        char d = DelimChar(o.Delim); string fmt = "F" + o.Decimals; var inv = CultureInfo.InvariantCulture; Span<char> buf = stackalloc char[64];
        var line = new StringBuilder(cols * (o.Decimals + 8));
        for (int y = 0; y < rows; y++)
        {
            line.Clear();
            for (int x = 0; x < cols; x++)
            {
                if (x > 0) line.Append(d);
                float v = data[(long)y * cols + x];
                if (float.IsNaN(v)) { line.Append(o.OutsideAsZero ? "0" : "NaN"); continue; }
                double val = applyScale ? v * scale : v;
                if (val.TryFormat(buf, out int n, fmt, inv)) { if (n == 2 && buf[0] == '-' && buf[1] == '0') line.Append('0'); else line.Append(buf[..n]); }
                else line.Append(val.ToString(fmt, inv));
            }
            w.WriteLine(line.ToString());
        }
    }

    /// <summary>Integer matrix (mask) as text.</summary>
    public static void WriteMaskText(string path, byte[] mask, int rows, int cols, MatrixOptions o, IEnumerable<string>? headerLines = null)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        using var w = new StreamWriter(fs, new ASCIIEncoding(), 1 << 20) { NewLine = "\n" };
        if (o.Header && headerLines != null) foreach (var h in headerLines) w.WriteLine("# " + h);
        char d = DelimChar(o.Delim); var line = new StringBuilder(cols * 2);
        for (int y = 0; y < rows; y++) { line.Clear(); for (int x = 0; x < cols; x++) { if (x > 0) line.Append(d); line.Append(mask[(long)y * cols + x] != 0 ? '1' : '0'); } w.WriteLine(line.ToString()); }
    }

    /// <summary>x_mm, y_mm, value columns. Origin = centroid of the mask, x to the right, y down (image orientation).</summary>
    public static void WriteXyz(string path, float[] data, byte[] mask, int M, double pitchM, double scale, MatrixOptions o, bool dropMasked, IEnumerable<string>? headerLines = null, string valueName = "OPD")
    {
        double sx = 0, sy = 0; long c = 0;
        for (int y = 0; y < M; y++) for (int x = 0; x < M; x++) if (mask[(long)y * M + x] != 0) { sx += x; sy += y; c++; }
        double cx = c > 0 ? sx / c : M / 2.0, cy = c > 0 ? sy / c : M / 2.0; var inv = CultureInfo.InvariantCulture; char d = DelimChar(o.Delim);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        using var w = new StreamWriter(fs, new ASCIIEncoding(), 1 << 20) { NewLine = "\n" };
        if (o.Header && headerLines != null) { foreach (var h in headerLines) w.WriteLine("# " + h); w.WriteLine($"# x_mm{d}y_mm{d}{valueName}_{UnitName(o.Unit)}   (origin = mask centroid, x right, y down)"); }
        string fmt = "F" + o.Decimals;
        for (int y = 0; y < M; y++) for (int x = 0; x < M; x++)
        {
            long i = (long)y * M + x; bool m = mask[i] != 0 && !float.IsNaN(data[i]); if (!m && dropMasked) continue;
            string v = m ? (data[i] * scale).ToString(fmt, inv) : (o.OutsideAsZero ? "0" : "NaN");
            w.WriteLine(((x - cx) * pitchM * 1e3).ToString("F5", inv) + d + ((y - cy) * pitchM * 1e3).ToString("F5", inv) + d + v);
        }
    }

    // ------------------------------------------------------------------------------------------------ NPY
    static void NpyHeader(Stream s, string descr, int rows, int cols)
    {
        string dict = $"{{'descr': '{descr}', 'fortran_order': False, 'shape': ({rows}, {cols}), }}";
        int total = 10 + dict.Length + 1; int pad = (64 - total % 64) % 64; dict = dict + new string(' ', pad) + "\n";
        s.Write(new byte[] { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0 }); s.Write(BitConverter.GetBytes((ushort)dict.Length)); s.Write(Encoding.ASCII.GetBytes(dict));
    }
    static void WriteFloats(Stream s, float[] a)
    {
        var buf = new byte[1 << 20]; long i = 0;
        while (i < a.Length) { int n = (int)Math.Min(buf.Length / 4, a.Length - i); Buffer.BlockCopy(a, (int)(i * 4), buf, 0, n * 4); s.Write(buf, 0, n * 4); i += n; }
    }
    public static void WriteNpyF32(string path, float[] a, int rows, int cols) { using var fs = File.Create(path); NpyHeader(fs, "<f4", rows, cols); WriteFloats(fs, a); }
    /// <summary>complex64 interleaved (re,im) array.</summary>
    public static void WriteNpyC64(string path, float[] interleaved, int rows, int cols) { using var fs = File.Create(path); NpyHeader(fs, "<c8", rows, cols); WriteFloats(fs, interleaved); }
    public static void WriteNpyU8(string path, byte[] a, int rows, int cols) { using var fs = File.Create(path); NpyHeader(fs, "|u1", rows, cols); fs.Write(a); }

    // ------------------------------------------------------------------------------------------------ TIFF
    /// <summary>32-bit IEEE float, uncompressed, single-channel TIFF (NaN preserved).</summary>
    public static void WriteTiffF32(string path, float[] a, int rows, int cols, double scale = 1.0)
    {
        using var t = Tiff.Open(path, "w") ?? throw new IOException("cannot create " + path);
        t.SetField(TiffTag.IMAGEWIDTH, cols); t.SetField(TiffTag.IMAGELENGTH, rows); t.SetField(TiffTag.SAMPLESPERPIXEL, 1); t.SetField(TiffTag.BITSPERSAMPLE, 32);
        t.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.IEEEFP); t.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK); t.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        t.SetField(TiffTag.COMPRESSION, Compression.NONE); t.SetField(TiffTag.ROWSPERSTRIP, 64);
        var row = new float[cols]; var bytes = new byte[cols * 4];
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++) row[x] = (float)(a[(long)y * cols + x] * scale);
            Buffer.BlockCopy(row, 0, bytes, 0, bytes.Length); if (!t.WriteScanline(bytes, y)) throw new IOException("tiff write failed");
        }
    }

    // ------------------------------------------------------------------------------------------------ MAT v5
    /// <summary>Minimal MATLAB level-5 MAT-file writer (uncompressed numeric matrices; column-major, so MATLAB sees the same image orientation).</summary>
    public sealed class MatFile : IDisposable
    {
        readonly BinaryWriter w;
        public MatFile(string path)
        {
            w = new BinaryWriter(File.Create(path));
            var txt = Encoding.ASCII.GetBytes("MATLAB 5.0 MAT-file, written by QwlsiApp".PadRight(116)); w.Write(txt); w.Write(new byte[8]); w.Write((ushort)0x0100); w.Write((byte)'I'); w.Write((byte)'M');
        }
        static int Pad8(int n) => (8 - n % 8) % 8;
        void Element(int type, byte[] payload) { w.Write(type); w.Write(payload.Length); w.Write(payload); w.Write(new byte[Pad8(payload.Length)]); }
        static byte[] Bytes(float[] a, int rows, int cols, Func<int, float> pick, bool colMajor = true)
        {
            var b = new byte[rows * cols * 4]; int k = 0;
            for (int c = 0; c < cols; c++) for (int r = 0; r < rows; r++) { BitConverter.TryWriteBytes(b.AsSpan(k * 4), pick(r * cols + c)); k++; }
            return b;
        }
        void Matrix(string name, int rows, int cols, int mxClass, bool complex, byte[] real, int dataType, byte[]? imag)
        {
            using var ms = new MemoryStream(); using var bw = new BinaryWriter(ms);
            void El(int type, byte[] payload) { bw.Write(type); bw.Write(payload.Length); bw.Write(payload); bw.Write(new byte[Pad8(payload.Length)]); }
            El(6, BitConverter.GetBytes((uint)(mxClass | (complex ? 0x800 : 0))).Concat(new byte[4]).ToArray());
            El(5, BitConverter.GetBytes(rows).Concat(BitConverter.GetBytes(cols)).ToArray());
            El(1, Encoding.ASCII.GetBytes(name));
            El(dataType, real); if (complex && imag != null) El(dataType, imag);
            Element(14, ms.ToArray());
        }
        public void Single(string name, float[] rowMajor, int rows, int cols, bool nanToZero = false)
            => Matrix(name, rows, cols, 7, false, Bytes(rowMajor, rows, cols, i => nanToZero && float.IsNaN(rowMajor[i]) ? 0f : rowMajor[i]), 7, null);
        public void ComplexSingle(string name, float[] interleaved, int rows, int cols)
            => Matrix(name, rows, cols, 7, true, Bytes(interleaved, rows, cols, i => interleaved[2 * i]), 7, Bytes(interleaved, rows, cols, i => interleaved[2 * i + 1]));
        public void UInt8(string name, byte[] rowMajor, int rows, int cols)
        {
            var b = new byte[rows * cols]; int k = 0; for (int c = 0; c < cols; c++) for (int r = 0; r < rows; r++) b[k++] = rowMajor[r * cols + c];
            Matrix(name, rows, cols, 9, false, b, 2, null);
        }
        public void Double(string name, params double[] values)
        {
            var b = new byte[values.Length * 8]; for (int i = 0; i < values.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 8), values[i]);
            Matrix(name, 1, values.Length, 6, false, b, 9, null);
        }
        public void Dispose() => w.Dispose();
    }
}
