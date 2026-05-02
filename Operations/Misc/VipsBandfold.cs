using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Reshape an <c>(W, H, B)</c> image into <c>(W/factor, H, B*factor)</c>
/// — fold consecutive pixels along the X axis onto extra bands.
///
/// <para>Mirrors libvips <c>vips_bandfold</c>. The byte layout is
/// pel-interleaved on both sides, so this is a pure metadata reshape:
/// rows are copied through unchanged, only <c>Width</c> and
/// <c>Bands</c> change. Useful for "treat 12 successive samples as a
/// 12-channel pixel" without an actual transform.</para>
///
/// <para>Default <c>Factor = 0</c> means "fold the whole row into bands"
/// — output is <c>(1, H, W*inBands)</c>.</para>
/// </summary>
public class VipsBandfold : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int Factor { get; set; } = 0;

    public override int Build()
    {
        if (In == null) return -1;
        int factor = Factor == 0 ? In.Width : Factor;
        if (factor < 1) return -1;
        if (In.Width % factor != 0) return -1;

        Out = new VipsImage
        {
            Width = In.Width / factor,
            Height = In.Height,
            Bands = In.Bands * factor,
            BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = factor,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Bandfold", RuntimeHelpers.GetHashCode(In), Factor);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        int factor = (int)b!;
        VipsRect r = outRegion.Valid;
        VipsImage @in = inRegion.Image;
        int sampleSize = @in.BandFormat == VipsBandFormat.Float ? 4 : 1;
        int inBands = @in.Bands;
        int outBands = inBands * factor;
        int outPel = outBands * sampleSize;
        int inPel = inBands * sampleSize;

        // Output pel (x, y) covers input pels (x*factor .. x*factor+factor-1, y).
        var inRect = new VipsRect(r.Left * factor, r.Top, r.Width * factor, r.Height);
        if (inRegion.Prepare(inRect) != 0) return -1;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(inRect.Left, inRect.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            // factor input pels in a row map to one output pel — total
            // bytes per scanline match.
            inAddr.Slice(0, r.Width * outPel).CopyTo(outAddr);
        }
        return 0;
    }
}

/// <summary>
/// Reshape <c>(W, H, B*factor)</c> back to <c>(W*factor, H, B)</c> — the
/// inverse of <see cref="VipsBandfold"/>. Mirrors libvips
/// <c>vips_bandunfold</c>. Default <c>Factor = 0</c> unfolds all bands
/// into a 1-band image of width <c>W*B</c>.
/// </summary>
public class VipsBandunfold : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int Factor { get; set; } = 0;

    public override int Build()
    {
        if (In == null) return -1;
        int factor = Factor == 0 ? In.Bands : Factor;
        if (factor < 1) return -1;
        if (In.Bands % factor != 0) return -1;

        Out = new VipsImage
        {
            Width = In.Width * factor,
            Height = In.Height,
            Bands = In.Bands / factor,
            BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = factor,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Bandunfold", RuntimeHelpers.GetHashCode(In), Factor);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        int factor = (int)b!;
        VipsRect r = outRegion.Valid;
        VipsImage @in = inRegion.Image;
        int sampleSize = @in.BandFormat == VipsBandFormat.Float ? 4 : 1;
        int outBands = @in.Bands / factor;

        // Output pels (x*factor .. x*factor+factor-1, y) come from input pel (x, y).
        // We need ceiling-divide on the lower bound and ceiling on the upper.
        int srcLeft = r.Left / factor;
        int srcRight = (r.Left + r.Width + factor - 1) / factor;
        var inRect = new VipsRect(srcLeft, r.Top, srcRight - srcLeft, r.Height);
        if (inRegion.Prepare(inRect) != 0) return -1;

        int outRowBytes = r.Width * outBands * sampleSize;
        int outOffsetWithinSrcRow = (r.Left - srcLeft * factor) * outBands * sampleSize;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(inRect.Left, inRect.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            inAddr.Slice(outOffsetWithinSrcRow, outRowBytes).CopyTo(outAddr);
        }
        return 0;
    }
}
