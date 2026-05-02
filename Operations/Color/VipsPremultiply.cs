using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

public enum VipsAlphaMode
{
    Premultiply = 0,
    Unpremultiply = 1,
}

/// <summary>
/// Alpha premultiplication / unpremultiplication. Multiplies (or divides)
/// each colour band by the alpha band so that downstream ops blend in
/// the correct alpha-correct space. Without this, masking and resize
/// operations on alpha-bearing images produce dark fringes around the
/// alpha edges.
///
/// <para>UChar branch treats alpha as a fraction of 255 — multiply
/// scales the colour by <c>alpha / 255</c>, divide scales by
/// <c>255 / alpha</c> with a guard at alpha = 0. Float branch uses the
/// libvips Float convention where alpha is nominal <c>[0, 1]</c> and
/// the multiply is direct (no /255 scaling).</para>
///
/// <para>Operates on 2-band (grey + alpha) and 4-band (RGB + alpha)
/// inputs. Other band counts pass through unchanged — no alpha to
/// premultiply against. Mirrors libvips <c>vips_premultiply</c> /
/// <c>vips_unpremultiply</c>.</para>
/// </summary>
public class VipsPremultiply : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public VipsAlphaMode Mode { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = In.Bands,
            BandFormat = In.BandFormat, Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = Mode
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Premultiply", RuntimeHelpers.GetHashCode(In), Mode);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        VipsAlphaMode mode = (VipsAlphaMode)b!;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        int bands = @in.Bands;
        int pelSize = @in.SizeOfPel;
        bool hasAlpha = bands == 2 || bands == 4;

        // No alpha → pass-through copy. Cheaper than re-walking pixels.
        if (!hasAlpha)
        {
            for (int y = 0; y < r.Height; y++)
            {
                var src = inRegion.GetAddress(r.Left, r.Top + y);
                var dst = outRegion.GetAddress(r.Left, r.Top + y);
                src.Slice(0, r.Width * pelSize).CopyTo(dst);
            }
            return 0;
        }

        int colorBands = bands - 1;
        int alphaIdx = colorBands;

        if (@in.BandFormat == VipsBandFormat.Float)
            return GenerateFloat(inRegion, outRegion, r, bands, colorBands, alphaIdx, mode);

        return GenerateUChar(inRegion, outRegion, r, bands, pelSize, colorBands, alphaIdx, mode);
    }

    private static int GenerateUChar(VipsRegion inRegion, VipsRegion outRegion, VipsRect r,
        int bands, int pelSize, int colorBands, int alphaIdx, VipsAlphaMode mode)
    {
        for (int y = 0; y < r.Height; y++)
        {
            var src = inRegion.GetAddress(r.Left, r.Top + y);
            var dst = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int o = x * pelSize;
                byte alpha = src[o + alphaIdx];
                if (mode == VipsAlphaMode.Premultiply)
                {
                    // out_color = in_color * alpha / 255
                    for (int i = 0; i < colorBands; i++)
                        dst[o + i] = (byte)((src[o + i] * alpha + 127) / 255);
                }
                else
                {
                    // out_color = in_color * 255 / alpha (with alpha=0 guard)
                    if (alpha == 0)
                    {
                        for (int i = 0; i < colorBands; i++) dst[o + i] = 0;
                    }
                    else
                    {
                        for (int i = 0; i < colorBands; i++)
                            dst[o + i] = (byte)Math.Clamp((src[o + i] * 255) / alpha, 0, 255);
                    }
                }
                dst[o + alphaIdx] = alpha;
            }
        }
        return 0;
    }

    private static int GenerateFloat(VipsRegion inRegion, VipsRegion outRegion, VipsRect r,
        int bands, int colorBands, int alphaIdx, VipsAlphaMode mode)
    {
        for (int y = 0; y < r.Height; y++)
        {
            var src = inRegion.GetAddress(r.Left, r.Top + y);
            var dst = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int baseOff = x * bands * 4;
                float alpha = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(baseOff + alphaIdx * 4, 4));
                if (mode == VipsAlphaMode.Premultiply)
                {
                    for (int i = 0; i < colorBands; i++)
                    {
                        int off = baseOff + i * 4;
                        float v = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(off, 4));
                        BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(off, 4), v * alpha);
                    }
                }
                else
                {
                    if (alpha == 0)
                    {
                        for (int i = 0; i < colorBands; i++)
                            BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(baseOff + i * 4, 4), 0f);
                    }
                    else
                    {
                        for (int i = 0; i < colorBands; i++)
                        {
                            int off = baseOff + i * 4;
                            float v = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(off, 4));
                            BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(off, 4), v / alpha);
                        }
                    }
                }
                // Alpha passes through unchanged.
                src.Slice(baseOff + alphaIdx * 4, 4).CopyTo(dst.Slice(baseOff + alphaIdx * 4, 4));
            }
        }
        return 0;
    }
}
