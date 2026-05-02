using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Histogram matching — remap an input image so that its band-wise
/// CDF approximates the CDF of a reference image. Mirrors libvips
/// <c>vips_hist_match</c> (with the convenience that we run the full
/// pipeline: compute both histograms, build the LUT, apply it). The
/// libvips C version operates on two histogram images and returns just
/// the LUT; this version takes the source images directly.
///
/// <para>Input and reference must agree on band count and band format
/// (UChar). Width / height may differ.</para>
///
/// <para>Useful for white-balance transfer between paired captures,
/// for stitching tone-matched panoramas, and for matching the
/// statistics of a synthetic image to a target distribution.</para>
/// </summary>
public class VipsHistMatch : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Reference { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null || Reference == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;
        if (Reference.BandFormat != VipsBandFormat.UChar) return -1;
        if (In.Bands != Reference.Bands) return -1;

        // Compute per-band CDFs for both. Then for each input bin v,
        // find the smallest reference bin r such that cdfRef[r] ≥
        // cdfIn[v] — that's the matched value.
        var lut = BuildMatchingLut(In, Reference);

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = In.Bands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = lut,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("HistMatch", RuntimeHelpers.GetHashCode(In), RuntimeHelpers.GetHashCode(Reference));

    private static byte[,] BuildMatchingLut(VipsImage input, VipsImage reference)
    {
        int bands = input.Bands;

        var inHist = HistogramOf(input);
        var refHist = HistogramOf(reference);

        // Cumulative counts, normalised to [0, 1].
        var inCdf = new double[bands, 256];
        var refCdf = new double[bands, 256];
        for (int bnd = 0; bnd < bands; bnd++)
        {
            long inSum = 0, refSum = 0;
            for (int i = 0; i < 256; i++) { inSum += inHist[bnd, i]; refSum += refHist[bnd, i]; }
            long inAcc = 0, refAcc = 0;
            for (int i = 0; i < 256; i++)
            {
                inAcc += inHist[bnd, i];
                refAcc += refHist[bnd, i];
                inCdf[bnd, i] = inSum > 0 ? (double)inAcc / inSum : 0;
                refCdf[bnd, i] = refSum > 0 ? (double)refAcc / refSum : 0;
            }
        }

        // For each input bin, walk the reference CDF until it catches up.
        var lut = new byte[bands, 256];
        for (int bnd = 0; bnd < bands; bnd++)
        {
            int j = 0;
            for (int v = 0; v < 256; v++)
            {
                double target = inCdf[bnd, v];
                while (j < 255 && refCdf[bnd, j] < target) j++;
                lut[bnd, v] = (byte)j;
            }
        }
        return lut;
    }

    private static long[,] HistogramOf(VipsImage img)
    {
        byte[] pixels;
        if (img.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(img);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }
        int W = img.Width, H = img.Height, bands = img.Bands;
        var hist = new long[bands, 256];
        for (int y = 0; y < H; y++)
        {
            int rowBase = y * W * bands;
            for (int x = 0; x < W; x++)
                for (int bnd = 0; bnd < bands; bnd++)
                    hist[bnd, pixels[rowBase + x * bands + bnd]]++;
        }
        return hist;
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        var lut = (byte[,])b!;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        int bands = @in.Bands;
        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int pel = x * bands;
                for (int bnd = 0; bnd < bands; bnd++)
                    outAddr[pel + bnd] = lut[bnd, inAddr[pel + bnd]];
            }
        }
        return 0;
    }
}
