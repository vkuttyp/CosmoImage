using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Rank-statistic across N input images. Output pixel <c>(x, y, b)</c>
/// is the <c>Index</c>-th sorted value of
/// <c>[in_0(x, y, b), in_1(x, y, b), …, in_{N-1}(x, y, b)]</c>.
///
/// <para>Default <c>Index = -1</c> means "median" — <c>N/2</c>. Common
/// uses: per-pixel median across multiple captures (denoising), or
/// finding the dimmest/brightest of a stack
/// (<c>Index = 0</c> / <c>Index = N-1</c>).</para>
///
/// <para>Mirrors libvips <c>vips_bandrank</c>. All inputs must agree on
/// dimensions, band count, and band format. UChar + Float branches.</para>
/// </summary>
public class VipsBandrank : VipsOperation
{
    public VipsImage[]? Inputs { get; set; }
    public VipsImage? Out { get; set; }
    /// <summary>Sorted-position index. -1 selects the median.</summary>
    public int Index { get; set; } = -1;

    public override int Build()
    {
        if (Inputs == null || Inputs.Length == 0) return -1;
        if (Inputs.Length == 1) { Out = Inputs[0]; return 0; }

        var first = Inputs[0];
        foreach (var img in Inputs)
        {
            if (img == null) return -1;
            if (img.Width != first.Width || img.Height != first.Height) return -1;
            if (img.Bands != first.Bands || img.BandFormat != first.BandFormat) return -1;
        }

        int idx = Index < 0 ? Inputs.Length / 2 : Index;
        if (idx < 0 || idx >= Inputs.Length) return -1;

        Out = new VipsImage
        {
            Width = first.Width, Height = first.Height, Bands = first.Bands,
            BandFormat = first.BandFormat,
            Interpretation = first.Interpretation,
            Coding = first.Coding, XRes = first.XRes, YRes = first.YRes,
            StartFn = VipsSeq.StartMany, GenerateFn = Generate, StopFn = VipsSeq.StopMany,
            ClientA = Inputs, ClientB = idx,
        };
        Out.CopyMetadataFrom(first);
        Out.SetPipeline(VipsDemandStyle.Any, Inputs);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("Bandrank"); h.Add(Index);
        if (Inputs != null) foreach (var i in Inputs) h.Add(RuntimeHelpers.GetHashCode(i));
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        int idx = (int)b!;
        VipsRect r = outRegion.Valid;
        int N = regions.Length;
        for (int i = 0; i < N; i++)
            if (regions[i].Prepare(r) != 0) return -1;

        VipsImage @in = regions[0].Image;
        int bands = @in.Bands;
        bool isFloat = @in.BandFormat == VipsBandFormat.Float;

        if (isFloat)
        {
            var buf = new float[N];
            for (int y = 0; y < r.Height; y++)
            {
                var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
                for (int x = 0; x < r.Width; x++)
                {
                    for (int bnd = 0; bnd < bands; bnd++)
                    {
                        for (int i = 0; i < N; i++)
                        {
                            var inAddr = regions[i].GetAddress(r.Left + x, r.Top + y);
                            buf[i] = BinaryPrimitives.ReadSingleLittleEndian(
                                inAddr.Slice(bnd * 4, 4));
                        }
                        Array.Sort(buf);
                        BinaryPrimitives.WriteSingleLittleEndian(
                            outAddr.Slice((x * bands + bnd) * 4, 4), buf[idx]);
                    }
                }
            }
            return 0;
        }

        // UChar — small N, insertion sort beats array machinery.
        var bbuf = new byte[N];
        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    for (int i = 0; i < N; i++)
                    {
                        var inAddr = regions[i].GetAddress(r.Left + x, r.Top + y);
                        bbuf[i] = inAddr[bnd];
                    }
                    // Insertion sort of N values.
                    for (int i = 1; i < N; i++)
                    {
                        byte k = bbuf[i]; int j = i - 1;
                        while (j >= 0 && bbuf[j] > k) { bbuf[j + 1] = bbuf[j]; j--; }
                        bbuf[j + 1] = k;
                    }
                    outAddr[x * bands + bnd] = bbuf[idx];
                }
            }
        }
        return 0;
    }
}
