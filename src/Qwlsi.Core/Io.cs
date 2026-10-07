using System.Text;
using BitMiracle.LibTiff.Classic;

namespace Qwlsi.Core;

public static class TiffIO
{
    /// <summary>Reads a single-page grayscale 8/16-bit TIFF into float32 (row-major).</summary>
    public static float[] ReadFrame(string path, out int width, out int height)
    {
        using var tif = Tiff.Open(path, "r") ?? throw new IOException("cannot open " + path);
        width = tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt(); height = tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        int bps = tif.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt();
        if (bps != 8 && bps != 16) throw new NotSupportedException($"{bps}-bit TIFF");
        var data = new float[(long)width * height];
        var line = new byte[tif.ScanlineSize()];
        for (int y = 0; y < height; y++)
        {
            if (!tif.ReadScanline(line, y)) throw new IOException("scanline " + y);
            long o = (long)y * width;
            if (bps == 16) for (int x = 0; x < width; x++) data[o + x] = BitConverter.ToUInt16(line, 2 * x);
            else for (int x = 0; x < width; x++) data[o + x] = line[x];
        }
        return data;
    }
}

/// <summary>Minimal .npy v1/v2 reader for float32 / complex64 C-order arrays (golden data).</summary>
public static class Npy
{
    public static float[] ReadFloat32(string path, out int[] shape, out bool complex)
    {
        using var fs = File.OpenRead(path); using var br = new BinaryReader(fs);
        var magic = br.ReadBytes(6); if (magic[0] != 0x93 || magic[1] != (byte)'N') throw new InvalidDataException("not npy");
        byte major = br.ReadByte(); br.ReadByte();
        int hl = major == 1 ? br.ReadUInt16() : (int)br.ReadUInt32();
        string hdr = Encoding.ASCII.GetString(br.ReadBytes(hl));
        complex = hdr.Contains("<c8");
        if (!complex && !hdr.Contains("<f4")) throw new NotSupportedException("npy dtype: " + hdr);
        if (hdr.Contains("'fortran_order': True")) throw new NotSupportedException("fortran order");
        int a = hdr.IndexOf("'shape': (") + 10, b = hdr.IndexOf(')', a);
        shape = hdr[a..b].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
        long n = 1; foreach (var s in shape) n *= s; if (complex) n *= 2;
        var data = new float[n]; var bytes = br.ReadBytes((int)(n * 4));
        Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length); return data;
    }
}
