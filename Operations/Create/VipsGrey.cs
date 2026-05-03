using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// Synthesise a horizontal grey ramp from 0 to 1 across the width.
/// Float single-band; output is constant down each column. Mirrors
/// libvips <c>vips_grey</c>.
///
/// <para>The simplest test pattern — feed it through any tone op
/// (<c>Linear</c>, <c>Pow</c>, a built tone-curve) to visualise the
/// transfer characteristic.</para>
/// </summary>
public class VipsGrey : VipsOperation
{
    public VipsImage? Out { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>If true, output is UChar 0..255 instead of Float 0..1.</summary>
    public bool UChar { get; set; }

    public override int Build()
    {
        if (Width < 1 || Height < 1) return -1;

        var format = UChar ? VipsBandFormat.UChar : VipsBandFormat.Float;
        Out = new VipsImage
        {
            Width = Width, Height = Height, Bands = 1, BandFormat = format,
            Interpretation = VipsInterpretation.BW,
            XRes = 1, YRes = 1,
            GenerateFn = Generate, ClientB = (Width, UChar),
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Grey", Width, Height, UChar);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var (W, isUChar) = ((int, bool))b!;
        VipsRect r = outRegion.Valid;
        for (int y = 0; y < r.Height; y++)
        {
            var addr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int gx = r.Left + x;
                if (isUChar)
                    addr[x] = (byte)Math.Clamp(gx * 255 / Math.Max(1, W - 1), 0, 255);
                else
                    BinaryPrimitives.WriteSingleLittleEndian(addr.Slice(x * 4, 4),
                        (float)gx / Math.Max(1, W - 1));
            }
        }
        return 0;
    }
}
