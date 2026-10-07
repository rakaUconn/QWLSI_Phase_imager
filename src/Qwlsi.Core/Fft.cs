using System.Runtime.InteropServices;

namespace Qwlsi.Core;

/// <summary>Thin P/Invoke layer over Intel MKL DFTI (single precision). MKL_LONG is 32-bit in the LP64 interface used by mkl_rt.</summary>
internal static unsafe class Mkl
{
    const string Lib = "mkl_rt.3.dll";
    [DllImport(Lib)] public static extern int DftiCreateDescriptor_s_md(out IntPtr h, int domain, int dim, int* sizes);
    [DllImport(Lib)] public static extern int DftiSetValue(IntPtr h, int param, int v);
    [DllImport(Lib)] public static extern int DftiSetValue(IntPtr h, int param, int* v);
    [DllImport(Lib)] public static extern int DftiCommitDescriptor(IntPtr h);
    [DllImport(Lib)] public static extern int DftiComputeForward(IntPtr h, void* a, void* b);
    [DllImport(Lib)] public static extern int DftiComputeBackward(IntPtr h, void* a, void* b);
    [DllImport(Lib)] public static extern int DftiFreeDescriptor(ref IntPtr h);
    [DllImport(Lib)] public static extern void MKL_Set_Num_Threads(int n);

    public const int REAL = 33, COMPLEX = 32, PLACEMENT = 11, NOT_INPLACE = 44, CE_STORAGE = 10, COMPLEX_COMPLEX = 39, IN_STRIDES = 12, OUT_STRIDES = 13;
    public static void Check(int rc, string what) { if (rc != 0) throw new InvalidOperationException($"MKL {what} failed rc={rc}"); }
}

/// <summary>In-place n x n complex 2-D FFT on interleaved float32 (re,im). Backward is UNSCALED.</summary>
public sealed unsafe class ComplexFft2D : IDisposable
{
    IntPtr h; public readonly int N;
    public ComplexFft2D(int n)
    {
        N = n; int* sz = stackalloc int[2]; sz[0] = n; sz[1] = n;
        Mkl.Check(Mkl.DftiCreateDescriptor_s_md(out h, Mkl.COMPLEX, 2, sz), "create c2c");
        Mkl.Check(Mkl.DftiCommitDescriptor(h), "commit c2c");
    }
    public void Forward(float[] a) { fixed (float* p = a) Mkl.Check(Mkl.DftiComputeForward(h, p, null), "fwd c2c"); }
    public void Backward(float[] a) { fixed (float* p = a) Mkl.Check(Mkl.DftiComputeBackward(h, p, null), "bwd c2c"); }
    public void Dispose() { if (h != IntPtr.Zero) Mkl.DftiFreeDescriptor(ref h); }
}

/// <summary>Out-of-place n x n real &lt;-> n x (n/2+1) complex (conjugate-even) 2-D FFT. Backward is UNSCALED.</summary>
public sealed unsafe class RealFft2D : IDisposable
{
    IntPtr hf, hb; public readonly int N; public int Cols => N / 2 + 1;
    public RealFft2D(int n)
    {
        N = n; int* sz = stackalloc int[2]; sz[0] = n; sz[1] = n;
        int* rs = stackalloc int[3]; rs[0] = 0; rs[1] = n; rs[2] = 1;
        int* cs = stackalloc int[3]; cs[0] = 0; cs[1] = n / 2 + 1; cs[2] = 1;
        Mkl.Check(Mkl.DftiCreateDescriptor_s_md(out hf, Mkl.REAL, 2, sz), "create r2c");
        Mkl.Check(Mkl.DftiSetValue(hf, Mkl.PLACEMENT, Mkl.NOT_INPLACE), "place");
        Mkl.Check(Mkl.DftiSetValue(hf, Mkl.CE_STORAGE, Mkl.COMPLEX_COMPLEX), "storage");
        Mkl.Check(Mkl.DftiSetValue(hf, Mkl.IN_STRIDES, rs), "instr"); Mkl.Check(Mkl.DftiSetValue(hf, Mkl.OUT_STRIDES, cs), "outstr");
        Mkl.Check(Mkl.DftiCommitDescriptor(hf), "commit r2c");
        // backward descriptor: for backward transforms MKL reads the strides with input = complex side, output = real side
        Mkl.Check(Mkl.DftiCreateDescriptor_s_md(out hb, Mkl.REAL, 2, sz), "create c2r");
        Mkl.Check(Mkl.DftiSetValue(hb, Mkl.PLACEMENT, Mkl.NOT_INPLACE), "place");
        Mkl.Check(Mkl.DftiSetValue(hb, Mkl.CE_STORAGE, Mkl.COMPLEX_COMPLEX), "storage");
        Mkl.Check(Mkl.DftiSetValue(hb, Mkl.IN_STRIDES, cs), "instr"); Mkl.Check(Mkl.DftiSetValue(hb, Mkl.OUT_STRIDES, rs), "outstr");
        Mkl.Check(Mkl.DftiCommitDescriptor(hb), "commit c2r");
    }
    public void Forward(float[] real, float[] cplx) { fixed (float* a = real) fixed (float* b = cplx) Mkl.Check(Mkl.DftiComputeForward(hf, a, b), "fwd r2c"); }
    public void Backward(float[] cplx, float[] real) { fixed (float* a = cplx) fixed (float* b = real) Mkl.Check(Mkl.DftiComputeBackward(hb, a, b), "bwd c2r"); }
    public void Dispose() { if (hf != IntPtr.Zero) Mkl.DftiFreeDescriptor(ref hf); if (hb != IntPtr.Zero) Mkl.DftiFreeDescriptor(ref hb); }
}
