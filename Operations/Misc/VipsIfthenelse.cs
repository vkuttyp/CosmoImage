using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Per-pixel ternary: pick from <c>then</c> where the condition image is
/// non-zero, from <c>else</c> elsewhere.
///
/// <para>Mirrors libvips <c>vips_ifthenelse</c>. Conv: condition is
/// UChar (one or N bands); <c>then</c> and <c>else</c> share dimensions,
/// band-format, and band count, and the output inherits all three.
/// If the condition has 1 band but then/else have N, the condition is
/// broadcast across bands. If it has N matching bands, selection is
/// per-band (libvips' <c>blend=FALSE</c> behaviour).</para>
/// </summary>
public class VipsIfthenelse : VipsOperation
{
    public VipsImage? Condition { get; set; }
    public VipsImage? Then { get; set; }
    public VipsImage? Else { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (Condition == null || Then == null || Else == null) return -1;
        if (Condition.BandFormat != VipsBandFormat.UChar) return -1;
        if (Then.Width != Else.Width || Then.Height != Else.Height) return -1;
        if (Then.Bands != Else.Bands || Then.BandFormat != Else.BandFormat) return -1;
        if (Condition.Width != Then.Width || Condition.Height != Then.Height) return -1;
        if (Condition.Bands != 1 && Condition.Bands != Then.Bands) return -1;

        Out = new VipsImage
        {
            Width = Then.Width, Height = Then.Height, Bands = Then.Bands,
            BandFormat = Then.BandFormat, Interpretation = Then.Interpretation,
            Coding = Then.Coding, XRes = Then.XRes, YRes = Then.YRes,
            StartFn = VipsSeq.StartMany, GenerateFn = Generate, StopFn = VipsSeq.StopMany,
            ClientA = new[] { Condition, Then, Else },
        };
        Out.CopyMetadataFrom(Then);
        Out.SetPipeline(VipsDemandStyle.Any, Condition, Then, Else);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Ifthenelse",
            RuntimeHelpers.GetHashCode(Condition),
            RuntimeHelpers.GetHashCode(Then),
            RuntimeHelpers.GetHashCode(Else));

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var cond = regions[0]; var thn = regions[1]; var els = regions[2];
        VipsRect r = outRegion.Valid;

        if (cond.Prepare(r) != 0) return -1;
        if (thn.Prepare(r) != 0) return -1;
        if (els.Prepare(r) != 0) return -1;

        int bands = thn.Image.Bands;
        bool oneCond = cond.Image.Bands == 1;
        bool isFloat = thn.Image.BandFormat == VipsBandFormat.Float;
        int sampleSize = isFloat ? 4 : 1;
        int pelBytes = bands * sampleSize;

        for (int y = 0; y < r.Height; y++)
        {
            var ca = cond.GetAddress(r.Left, r.Top + y);
            var ta = thn.GetAddress(r.Left, r.Top + y);
            var ea = els.GetAddress(r.Left, r.Top + y);
            var oa = outRegion.GetAddress(r.Left, r.Top + y);

            for (int x = 0; x < r.Width; x++)
            {
                int pel = x * pelBytes;
                if (oneCond)
                {
                    bool pick = ca[x] != 0;
                    var src = pick ? ta : ea;
                    src.Slice(pel, pelBytes).CopyTo(oa.Slice(pel, pelBytes));
                }
                else
                {
                    int condPel = x * bands;
                    for (int bnd = 0; bnd < bands; bnd++)
                    {
                        bool pick = ca[condPel + bnd] != 0;
                        var src = pick ? ta : ea;
                        int off = pel + bnd * sampleSize;
                        src.Slice(off, sampleSize).CopyTo(oa.Slice(off, sampleSize));
                    }
                }
            }
        }
        return 0;
    }
}
