using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Per-pixel select from N source images, indexed by a single-band
/// UChar selector. <c>out(x, y) = cases[index(x, y)](x, y)</c>; if
/// <c>index ≥ N</c>, the output uses the last source as a default.
/// Mirrors libvips <c>vips_case</c>.
///
/// <para>Pairs naturally with <see cref="VipsSwitch"/>: the switch
/// computes the cell index, the case substitutes a value. All sources
/// must agree on dimensions, band-format, and band count.</para>
/// </summary>
public class VipsCase : VipsOperation
{
    public VipsImage? Index { get; set; }
    public VipsImage[]? Cases { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (Index == null || Cases == null || Cases.Length == 0) return -1;
        if (Index.BandFormat != VipsBandFormat.UChar || Index.Bands != 1) return -1;
        var first = Cases[0];
        foreach (var c in Cases)
        {
            if (c == null) return -1;
            if (c.Width != first.Width || c.Height != first.Height) return -1;
            if (c.Bands != first.Bands || c.BandFormat != first.BandFormat) return -1;
        }
        if (Index.Width != first.Width || Index.Height != first.Height) return -1;

        var inputs = new VipsImage[Cases.Length + 1];
        inputs[0] = Index;
        Array.Copy(Cases, 0, inputs, 1, Cases.Length);

        Out = new VipsImage
        {
            Width = first.Width, Height = first.Height,
            Bands = first.Bands, BandFormat = first.BandFormat,
            Interpretation = first.Interpretation,
            Coding = first.Coding, XRes = first.XRes, YRes = first.YRes,
            StartFn = VipsSeq.StartMany, GenerateFn = Generate, StopFn = VipsSeq.StopMany,
            ClientA = inputs, ClientB = Cases.Length,
        };
        Out.CopyMetadataFrom(first);
        Out.SetPipeline(VipsDemandStyle.Any, inputs);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("Case"); h.Add(RuntimeHelpers.GetHashCode(Index));
        if (Cases != null) foreach (var c in Cases) h.Add(RuntimeHelpers.GetHashCode(c));
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        int N = (int)b!;
        VipsRect r = outRegion.Valid;
        for (int i = 0; i < regions.Length; i++)
            if (regions[i].Prepare(r) != 0) return -1;

        var indexReg = regions[0];
        bool isFloat = regions[1].Image.BandFormat == VipsBandFormat.Float;
        int sampleSize = isFloat ? 4 : 1;
        int bands = regions[1].Image.Bands;
        int pelBytes = bands * sampleSize;

        for (int y = 0; y < r.Height; y++)
        {
            var idxAddr = indexReg.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int pick = idxAddr[x];
                if (pick >= N) pick = N - 1;
                var srcReg = regions[pick + 1];
                var srcAddr = srcReg.GetAddress(r.Left + x, r.Top + y);
                srcAddr.Slice(0, pelBytes).CopyTo(outAddr.Slice(x * pelBytes, pelBytes));
            }
        }
        return 0;
    }
}
