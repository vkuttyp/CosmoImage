using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Recombine bands by a square matrix M: <c>out[k] = Σ M[k,i] * in[i]</c>.
/// For RGBA-shaped inputs (matrix dim = bands - 1), alpha passes through
/// untouched. Building block for Saturate / Hue / Greyscale / Sepia.
/// Mirrors libvips' <c>vips_recomb</c>.
/// </summary>
public class VipsRecomb : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    /// <summary>Square matrix (N×N). N must equal <c>In.Bands</c> or <c>In.Bands - 1</c> (alpha passthrough).</summary>
    public double[,]? Matrix { get; set; }

    public override int Build()
    {
        if (In == null || Matrix == null) return -1;
        int rows = Matrix.GetLength(0);
        int cols = Matrix.GetLength(1);
        if (rows != cols) return -1;
        if (In.Bands != rows && In.Bands != rows + 1) return -1;

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
            ClientB = Matrix
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        var hash = new HashCode();
        hash.Add("Recomb");
        hash.Add(RuntimeHelpers.GetHashCode(In));
        if (Matrix != null) foreach (var v in Matrix) hash.Add(v);
        return hash.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        double[,] M = (double[,])b!;
        VipsRect r = outRegion.Valid;
        if (inRegion.Prepare(r) != 0) return -1;

        int colorBands = M.GetLength(0);
        int totalBands = @in.Bands;
        bool hasAlpha = totalBands > colorBands;

        // Flatten matrix for tighter inner loop indexing.
        var Mflat = new double[colorBands * colorBands];
        for (int o = 0; o < colorBands; o++)
            for (int i = 0; i < colorBands; i++)
                Mflat[o * colorBands + i] = M[o, i];

        if (@in.BandFormat == VipsBandFormat.Float)
            return GenerateFloat(inRegion, outRegion, r, totalBands, colorBands, hasAlpha, Mflat);

        int pelSize = @in.SizeOfPel;
        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);

            for (int x = 0; x < r.Width; x++)
            {
                int baseIdx = x * pelSize;
                for (int o = 0; o < colorBands; o++)
                {
                    double sum = 0;
                    int rowBase = o * colorBands;
                    for (int i = 0; i < colorBands; i++)
                        sum += Mflat[rowBase + i] * inAddr[baseIdx + i];
                    outAddr[baseIdx + o] = (byte)Math.Clamp(sum, 0, 255);
                }
                if (hasAlpha)
                    outAddr[baseIdx + colorBands] = inAddr[baseIdx + colorBands];
            }
        }
        return 0;
    }

    private static int GenerateFloat(VipsRegion inRegion, VipsRegion outRegion, VipsRect r, int totalBands, int colorBands, bool hasAlpha, double[] Mflat)
    {
        // Hoist the per-pixel scratch out of the loop — same buffer is reused
        // for every pixel in this Generate call.
        Span<float> input = stackalloc float[16]; // bands ≤ 16 in any plausible case

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int baseIdx = x * totalBands * 4;
                for (int i = 0; i < colorBands; i++)
                    input[i] = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(baseIdx + i * 4, 4));

                for (int o = 0; o < colorBands; o++)
                {
                    double sum = 0;
                    int rowBase = o * colorBands;
                    for (int i = 0; i < colorBands; i++)
                        sum += Mflat[rowBase + i] * input[i];
                    BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(baseIdx + o * 4, 4), (float)sum);
                }
                if (hasAlpha)
                {
                    var alphaSrc = inAddr.Slice(baseIdx + colorBands * 4, 4);
                    alphaSrc.CopyTo(outAddr.Slice(baseIdx + colorBands * 4, 4));
                }
            }
        }
        return 0;
    }
}
