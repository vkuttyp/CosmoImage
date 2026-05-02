using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// Synthesise an all-zero image of the requested size, band count, and
/// band format. Mirrors libvips <c>vips_black</c>.
///
/// <para>The cheapest possible source for testing pipelines, building
/// canvases for <see cref="Operations.Mosaicing.VipsInsert"/>, and
/// providing a zero-fill in compose ops. Output is materialise-on-
/// demand; nothing is allocated until the first <c>Generate</c>
/// call.</para>
/// </summary>
public class VipsBlack : VipsOperation
{
    public VipsImage? Out { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Bands { get; set; } = 1;
    public VipsBandFormat BandFormat { get; set; } = VipsBandFormat.UChar;

    public override int Build()
    {
        if (Width < 1 || Height < 1 || Bands < 1) return -1;

        Out = new VipsImage
        {
            Width = Width, Height = Height, Bands = Bands, BandFormat = BandFormat,
            Interpretation = Bands switch
            {
                1 or 2 => VipsInterpretation.BW,
                _ => VipsInterpretation.RGB,
            },
            XRes = 1, YRes = 1,
            GenerateFn = Generate,
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Black", Width, Height, Bands, BandFormat);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        VipsRect r = outRegion.Valid;
        int rowBytes = r.Width * outRegion.Image.SizeOfPel;
        for (int y = 0; y < r.Height; y++)
            outRegion.GetAddress(r.Left, r.Top + y).Slice(0, rowBytes).Clear();
        return 0;
    }
}
