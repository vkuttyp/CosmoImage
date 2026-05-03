using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Line Hough transform. For every pixel above
/// <see cref="Threshold"/>, vote in (ρ, θ) parameter space where
/// <c>ρ = x · cos(θ) + y · sin(θ)</c>. Local maxima in the output
/// correspond to detected lines. Mirrors libvips
/// <c>vips_hough_line</c>.
///
/// <para>Output is UInt single-band, width = <see cref="Width"/>
/// (θ bins, mapping to <c>[0, π)</c>), height = <see cref="Height"/>
/// (ρ bins, mapping to <c>[-diag, +diag]</c> where diag is the input
/// diagonal). UChar 1-band input.</para>
/// </summary>
public class VipsHoughLine : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int Width { get; set; } = 256;
    public int Height { get; set; } = 256;
    /// <summary>Pixel value above which to vote. Default 128 (mid-point).</summary>
    public int Threshold { get; set; } = 128;

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar || In.Bands != 1) return -1;
        if (Width < 1 || Height < 1) return -1;

        // Materialise input.
        byte[] pixels;
        if (In.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(In);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        int W = In.Width, H = In.Height;
        double diag = Math.Sqrt((double)W * W + (double)H * H);
        var accum = new uint[Width * Height];

        // Pre-compute sin/cos for each θ bin.
        var cosT = new double[Width];
        var sinT = new double[Width];
        for (int t = 0; t < Width; t++)
        {
            double theta = Math.PI * t / Width;
            cosT[t] = Math.Cos(theta);
            sinT[t] = Math.Sin(theta);
        }

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                if (pixels[y * W + x] <= Threshold) continue;
                for (int t = 0; t < Width; t++)
                {
                    double rho = x * cosT[t] + y * sinT[t];
                    int rIdx = (int)Math.Round((rho + diag) * (Height - 1) / (2 * diag));
                    if (rIdx >= 0 && rIdx < Height) accum[rIdx * Width + t]++;
                }
            }
        }

        var outBytes = new byte[Width * Height * 4];
        for (int i = 0; i < Width * Height; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(i * 4, 4), accum[i]);

        Out = new VipsImage
        {
            Width = Width, Height = Height, Bands = 1, BandFormat = VipsBandFormat.UInt,
            Interpretation = VipsInterpretation.Histogram,
            Coding = In.Coding, XRes = 1, YRes = 1,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = outBytes,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("HoughLine", RuntimeHelpers.GetHashCode(In),
            Width, Height, Threshold);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var buf = (byte[])b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        int W = @out.Width;
        for (int y = 0; y < r.Height; y++)
        {
            var addr = outRegion.GetAddress(r.Left, r.Top + y);
            int srcOff = ((r.Top + y) * W + r.Left) * 4;
            buf.AsSpan(srcOff, r.Width * 4).CopyTo(addr);
        }
        return 0;
    }
}
