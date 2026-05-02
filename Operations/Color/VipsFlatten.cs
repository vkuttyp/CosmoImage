using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Composite an alpha-bearing image onto an opaque background colour and
/// drop the alpha channel. <c>out_color = src_color * α + bg * (1 − α)</c>.
///
/// <para>UChar branch normalizes alpha by 255; Float branch treats alpha
/// as nominal <c>[0, 1]</c> per the libvips Float convention. Output band
/// count is <c>input.Bands − 1</c> for 2- or 4-band inputs (alpha
/// dropped); inputs without alpha pass through unchanged.</para>
///
/// <para>Mirrors libvips <c>vips_flatten</c>. The canonical use is
/// preparing an RGBA image for save into an alpha-less format (JPEG,
/// FITS, HDR, etc.) without leaving the dark/light fringes that a naive
/// alpha-strip produces.</para>
/// </summary>
public class VipsFlatten : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    /// <summary>Per-band background colour. Defaults to black if null.</summary>
    public double[]? Background { get; set; }

    public override int Build()
    {
        if (In == null) return -1;

        bool hasAlpha = In.Bands == 2 || In.Bands == 4;
        int outBands = hasAlpha ? In.Bands - 1 : In.Bands;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = outBands,
            BandFormat = In.BandFormat,
            Interpretation = outBands switch
            {
                1 => VipsInterpretation.BW,
                _ => VipsInterpretation.RGB,
            },
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = Background ?? Array.Empty<double>()
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("Flatten");
        h.Add(RuntimeHelpers.GetHashCode(In));
        if (Background != null) foreach (var c in Background) h.Add(c);
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        double[] bg = (double[])b!;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        int inBands = @in.Bands;
        bool hasAlpha = inBands == 2 || inBands == 4;
        int colorBands = hasAlpha ? inBands - 1 : inBands;

        // No alpha → pass-through copy.
        if (!hasAlpha)
        {
            int pelSize = @in.SizeOfPel;
            for (int y = 0; y < r.Height; y++)
            {
                var src = inRegion.GetAddress(r.Left, r.Top + y);
                var dst = outRegion.GetAddress(r.Left, r.Top + y);
                src.Slice(0, r.Width * pelSize).CopyTo(dst);
            }
            return 0;
        }

        if (@in.BandFormat == VipsBandFormat.Float)
            return GenerateFloat(inRegion, outRegion, r, inBands, colorBands, bg);

        return GenerateUChar(inRegion, outRegion, r, inBands, colorBands, bg);
    }

    private static int GenerateUChar(VipsRegion inRegion, VipsRegion outRegion, VipsRect r,
        int inBands, int colorBands, double[] bg)
    {
        // Build per-band background bytes. Default to black if the caller
        // passed an empty array.
        Span<byte> bgBytes = stackalloc byte[colorBands];
        for (int i = 0; i < colorBands; i++)
        {
            double v = bg.Length > 0 ? bg[i % bg.Length] : 0;
            bgBytes[i] = (byte)Math.Clamp(v, 0, 255);
        }

        int alphaIdx = colorBands;
        for (int y = 0; y < r.Height; y++)
        {
            var src = inRegion.GetAddress(r.Left, r.Top + y);
            var dst = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int srcOff = x * inBands;
                int dstOff = x * colorBands;
                byte alpha = src[srcOff + alphaIdx];
                if (alpha == 255)
                {
                    // Fully opaque — copy through.
                    src.Slice(srcOff, colorBands).CopyTo(dst.Slice(dstOff, colorBands));
                }
                else if (alpha == 0)
                {
                    // Fully transparent — pure background.
                    bgBytes.CopyTo(dst.Slice(dstOff, colorBands));
                }
                else
                {
                    // Blend. (color * alpha + bg * (255 - alpha) + 127) / 255
                    int invAlpha = 255 - alpha;
                    for (int i = 0; i < colorBands; i++)
                    {
                        int blend = (src[srcOff + i] * alpha + bgBytes[i] * invAlpha + 127) / 255;
                        dst[dstOff + i] = (byte)Math.Clamp(blend, 0, 255);
                    }
                }
            }
        }
        return 0;
    }

    private static int GenerateFloat(VipsRegion inRegion, VipsRegion outRegion, VipsRect r,
        int inBands, int colorBands, double[] bg)
    {
        int alphaIdx = colorBands;
        for (int y = 0; y < r.Height; y++)
        {
            var src = inRegion.GetAddress(r.Left, r.Top + y);
            var dst = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int srcBaseOff = x * inBands * 4;
                int dstBaseOff = x * colorBands * 4;
                float alpha = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(srcBaseOff + alphaIdx * 4, 4));
                float invAlpha = 1f - alpha;
                for (int i = 0; i < colorBands; i++)
                {
                    float v = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(srcBaseOff + i * 4, 4));
                    double bgi = bg.Length > 0 ? bg[i % bg.Length] : 0.0;
                    float blend = (float)(v * alpha + bgi * invAlpha);
                    BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(dstBaseOff + i * 4, 4), blend);
                }
            }
        }
        return 0;
    }
}
