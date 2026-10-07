using System.Diagnostics;
using System.Runtime.InteropServices;

// Phase-0 FFT benchmark: FFTW 3.3.5 (float, threaded) vs Intel MKL DFTI (single) on this PC.
static unsafe class Native
{
    const string FW = "libfftw3f-3.dll";
    [DllImport(FW)] public static extern int fftwf_init_threads();
    [DllImport(FW)] public static extern void fftwf_plan_with_nthreads(int n);
    [DllImport(FW)] public static extern IntPtr fftwf_plan_dft_r2c_2d(int n0, int n1, float* i, float* o, uint flags);
    [DllImport(FW)] public static extern IntPtr fftwf_plan_dft_c2r_2d(int n0, int n1, float* i, float* o, uint flags);
    [DllImport(FW)] public static extern IntPtr fftwf_plan_dft_2d(int n0, int n1, float* i, float* o, int sign, uint flags);
    [DllImport(FW)] public static extern void fftwf_execute(IntPtr p);
    [DllImport(FW)] public static extern void fftwf_destroy_plan(IntPtr p);

    const string MK = "mkl_rt.3.dll";
    [DllImport(MK)] public static extern int DftiCreateDescriptor_s_md(out IntPtr h, int domain, int dim, int* sizes);
    [DllImport(MK)] public static extern int DftiSetValue(IntPtr h, int param, int v);
    [DllImport(MK)] public static extern int DftiSetValue(IntPtr h, int param, int* v);
    [DllImport(MK)] public static extern int DftiCommitDescriptor(IntPtr h);
    [DllImport(MK)] public static extern int DftiComputeForward(IntPtr h, void* a, void* b);
    [DllImport(MK)] public static extern int DftiComputeBackward(IntPtr h, void* a, void* b);
    [DllImport(MK)] public static extern int DftiFreeDescriptor(ref IntPtr h);
    [DllImport(MK)] public static extern int MKL_Set_Num_Threads_Local(int n);
    [DllImport(MK)] public static extern void MKL_Set_Num_Threads(int n);
}

static unsafe class FftBench
{
    const uint FFTW_MEASURE = 0, FFTW_ESTIMATE = 1u << 6, FFTW_PATIENT = 1u << 5;
    static float* Alloc(long nfloats) { var p = (float*)NativeMemory.AlignedAlloc((nuint)(nfloats * 4), 64); return p; }

    static double Time(Action a, int reps)
    {
        a(); // warm
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < reps; i++) a();
        return sw.Elapsed.TotalMilliseconds / reps;
    }

    public static void Run(string[] args)
    {
        string fftwDir = args.Length > 0 ? args[0] : AppContext.BaseDirectory;
        int[] threadList = { 1, 4, 8, 16, 18, 36 };
        const int N = 4512, M = 1504;
        Console.WriteLine($"Logical procs: {Environment.ProcessorCount}");
        Native.fftwf_init_threads();

        // buffers
        float* rin = Alloc((long)N * N);
        float* rout = Alloc((long)N * (N / 2 + 1) * 2);
        float* cin = Alloc((long)M * M * 2);
        float* cout_ = Alloc((long)M * M * 2);
        var rnd = new Random(1);
        for (long i = 0; i < (long)N * N; i++) rin[i] = (float)rnd.NextDouble();
        for (long i = 0; i < (long)M * M * 2; i++) cin[i] = (float)rnd.NextDouble();

        Console.WriteLine("\n== FFTW 3.3.5 float ==  (ms per transform, mean of reps)");
        foreach (var flagsName in new[] { "ESTIMATE", "MEASURE" })
        {
            uint fl = flagsName == "ESTIMATE" ? FFTW_ESTIMATE : FFTW_MEASURE;
            foreach (int t in threadList)
            {
                Native.fftwf_plan_with_nthreads(t);
                var sw = Stopwatch.StartNew();
                float* tmpin = Alloc((long)N * N);   // planning with MEASURE overwrites buffers; use scratch
                IntPtr pr = Native.fftwf_plan_dft_r2c_2d(N, N, tmpin, rout, fl);
                double planR = sw.Elapsed.TotalSeconds; sw.Restart();
                IntPtr pc = Native.fftwf_plan_dft_2d(M, M, cin, cout_, -1, fl);
                double planC = sw.Elapsed.TotalSeconds;
                for (long i = 0; i < (long)N * N; i++) tmpin[i] = rin[i];
                for (long i = 0; i < (long)M * M * 2; i++) cin[i] = (float)(i % 17);
                // execute on new arrays requires same alignment; use planned arrays directly
                double tr = Time(() => Native.fftwf_execute(pr), 5);
                double tc = Time(() => Native.fftwf_execute(pc), 20);
                Console.WriteLine($"{flagsName,-8} thr={t,2}  r2c {N}^2: {tr,8:F1} ms (plan {planR,5:F1}s)   c2c {M}^2: {tc,7:F2} ms (plan {planC,4:F1}s)");
                Native.fftwf_destroy_plan(pr); Native.fftwf_destroy_plan(pc);
                NativeMemory.AlignedFree(tmpin);
            }
        }

        Console.WriteLine("\n== Intel MKL DFTI single ==");
        foreach (int t in threadList)
        {
            Native.MKL_Set_Num_Threads(t);
            int* szR = stackalloc int[2]; szR[0] = N; szR[1] = N;
            int r1 = Native.DftiCreateDescriptor_s_md(out IntPtr hr, 33 /*REAL*/, 2, szR);
            int r2 = Native.DftiSetValue(hr, 11, 44);  // NOT_INPLACE
            int r3 = Native.DftiSetValue(hr, 10, 39);  // CONJUGATE_EVEN_STORAGE = COMPLEX_COMPLEX
            int* inS = stackalloc int[3]; inS[0] = 0; inS[1] = N; inS[2] = 1;
            int* outS = stackalloc int[3]; outS[0] = 0; outS[1] = N / 2 + 1; outS[2] = 1;
            int r4 = Native.DftiSetValue(hr, 12, inS); int r5 = Native.DftiSetValue(hr, 13, outS);
            Console.WriteLine($"  create={r1} place={r2} ce={r3} instr={r4} outstr={r5}");
            var sw = Stopwatch.StartNew();
            int rc = Native.DftiCommitDescriptor(hr);
            double planR = sw.Elapsed.TotalSeconds;
            int* szC = stackalloc int[2]; szC[0] = M; szC[1] = M;
            int c1 = Native.DftiCreateDescriptor_s_md(out IntPtr hc, 32 /*COMPLEX*/, 2, szC);
            int c2 = Native.DftiSetValue(hc, 11, 44);
            int c3 = Native.DftiCommitDescriptor(hc);
            Console.WriteLine($"  c2c create={c1} place={c2} commit={c3}");
            double tr = Time(() => Native.DftiComputeForward(hr, rin, rout), 5);
            double tc = Time(() => Native.DftiComputeForward(hc, cin, cout_), 20);
            Console.WriteLine($"thr={t,2}  r2c {N}^2: {tr,8:F1} ms (commit rc={rc}, {planR:F2}s)   c2c {M}^2: {tc,7:F2} ms");
            Native.DftiFreeDescriptor(ref hr); Native.DftiFreeDescriptor(ref hc);
        }
    }
}
