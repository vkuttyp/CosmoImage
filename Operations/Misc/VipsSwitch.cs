using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Multi-way pixel test. Given <c>N</c> single-band UChar test images,
/// outputs the index of the first one that is non-zero at each pixel.
/// If none are true, the output is <c>N</c>. Mirrors libvips
/// <c>vips_switch</c>.
///
/// <para>Cheap building block for multi-way selection — combine with
/// <see cref="VipsCase"/> to pick from N source images, or with
/// <c>Maplut</c> to substitute per-cell colours.</para>
/// </summary>
public class VipsSwitch : VipsOperation
{
    public VipsImage[]? Tests { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (Tests == null || Tests.Length == 0) return -1;
        var first = Tests[0];
        foreach (var t in Tests)
        {
            if (t == null) return -1;
            if (t.BandFormat != VipsBandFormat.UChar) return -1;
            if (t.Bands != 1) return -1;
            if (t.Width != first.Width || t.Height != first.Height) return -1;
        }
        if (Tests.Length > 254) return -1; // need room for "no-match" sentinel < 256

        Out = new VipsImage
        {
            Width = first.Width, Height = first.Height, Bands = 1,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            Coding = first.Coding, XRes = first.XRes, YRes = first.YRes,
            StartFn = VipsSeq.StartMany, GenerateFn = Generate, StopFn = VipsSeq.StopMany,
            ClientA = Tests,
        };
        Out.CopyMetadataFrom(first);
        Out.SetPipeline(VipsDemandStyle.Any, Tests);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("Switch");
        if (Tests != null) foreach (var t in Tests) h.Add(RuntimeHelpers.GetHashCode(t));
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        int N = regions.Length;
        VipsRect r = outRegion.Valid;
        for (int i = 0; i < N; i++)
            if (regions[i].Prepare(r) != 0) return -1;

        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                byte pick = (byte)N;
                for (int i = 0; i < N; i++)
                {
                    if (regions[i].GetAddress(r.Left + x, r.Top + y)[0] != 0)
                    {
                        pick = (byte)i;
                        break;
                    }
                }
                outAddr[x] = pick;
            }
        }
        return 0;
    }
}
