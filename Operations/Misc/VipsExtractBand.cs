using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Pull <paramref name="N"/> consecutive bands starting at offset
/// <paramref name="Band"/> out of the input. Width / height / band-format
/// are preserved; only the band axis is sliced.
///
/// <para>Mirrors libvips <c>vips_extract_band</c>. Common use is
/// pulling individual channels out for per-channel processing
/// (<c>ExtractBand(rgb, 0, 1)</c> → R; <c>ExtractBand(rgba, 3, 1)</c> →
/// alpha-only, etc.).</para>
/// </summary>
public class VipsExtractBand : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int Band { get; set; }
    public int N { get; set; } = 1;

    public override int Build()
    {
        if (In == null) return -1;
        if (N < 1) return -1;
        if (Band < 0 || Band + N > In.Bands) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = N,
            BandFormat = In.BandFormat,
            Interpretation = N switch
            {
                1 or 2 => VipsInterpretation.BW,
                _ => In.Interpretation,
            },
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = (Band, N),
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("ExtractBand", RuntimeHelpers.GetHashCode(In), Band, N);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        var (band, n) = ((int, int))b!;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        int sampleSize = @in.BandFormat == VipsBandFormat.Float ? 4 : 1;
        int inBands = @in.Bands;
        int srcOff = band * sampleSize;
        int copyBytes = n * sampleSize;
        int inPel = inBands * sampleSize;
        int outPel = n * sampleSize;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
                inAddr.Slice(x * inPel + srcOff, copyBytes).CopyTo(outAddr.Slice(x * outPel, copyBytes));
        }
        return 0;
    }
}
