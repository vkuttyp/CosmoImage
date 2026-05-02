using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Numerics;

namespace CosmoImage.Operations.Color;

public class VipsLinear : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public double[]? A { get; set; }
    public double[]? B { get; set; }

    public override int Build()
    {
        if (In == null || A == null || B == null) return -1;

        Out = new VipsImage
        {
            Width = In.Width,
            Height = In.Height,
            Bands = In.Bands,
            BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding,
            XRes = In.XRes,
            YRes = In.YRes,
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In,
            ClientB = new { A, B }
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        var hash = new HashCode();
        hash.Add("Linear");
        hash.Add(RuntimeHelpers.GetHashCode(In));
        if (A != null) foreach (var a in A) hash.Add(a);
        if (B != null) foreach (var b in B) hash.Add(b);
        return hash.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = (VipsImage)a!;
        dynamic constants = b!;
        double[] A = constants.A;
        double[] B = constants.B;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        // Float pipeline: no clamp, full-precision linear transform.
        // Output format matches input — Build copies BandFormat unchanged.
        if (@in.BandFormat == VipsBandFormat.Float)
            return GenerateFloat(inRegion, outRegion, @in, A, B, r);

        int bands = @in.Bands;
        int totalBytes = r.Width * bands;
        bool allSame = A.Length == 1 && B.Length == 1;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);

            int i = 0;

            // T03: SIMD Optimization for the all-same case
            if (allSame)
            {
                float af = (float)A[0];
                float bf = (float)B[0];
                int vSize = Vector<float>.Count;
                var vA = new Vector<float>(af);
                var vB = new Vector<float>(bf);

                for (; i <= totalBytes - vSize; i += vSize)
                {
                    // Scalar expansion (fast enough for now, real vips uses specialized widening)
                    float[] floats = new float[vSize];
                    for (int j = 0; j < vSize; j++) floats[j] = inAddr[i + j];
                    var vIn = new Vector<float>(floats);
                    
                    var vRes = vIn * vA + vB;
                    
                    for (int j = 0; j < vSize; j++) outAddr[i + j] = (byte)Math.Clamp(vRes[j], 0, 255);
                }
            }

            // Remainder/Scalar path
            for (; i < totalBytes; i++)
            {
                int bIdx = i % bands;
                double val = inAddr[i];
                double res = val * (A.Length > bIdx ? A[bIdx] : A[0]) + (B.Length > bIdx ? B[bIdx] : B[0]);
                outAddr[i] = (byte)Math.Clamp(res, 0, 255);
            }
        }

        return 0;
    }

    private static int GenerateFloat(VipsRegion inRegion, VipsRegion outRegion, VipsImage @in, double[] A, double[] B, VipsRect r)
    {
        int bands = @in.Bands;
        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    int off = (x * bands + bnd) * 4;
                    float val = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(off, 4));
                    double a = A.Length > bnd ? A[bnd] : A[0];
                    double bb = B.Length > bnd ? B[bnd] : B[0];
                    float res = (float)(val * a + bb);
                    BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(off, 4), res);
                }
            }
        }
        return 0;
    }
}
