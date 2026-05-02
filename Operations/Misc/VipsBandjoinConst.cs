using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Append constant bands to the input. Each entry of
/// <see cref="C"/> becomes one extra band filled with that value. Output
/// has <c>In.Bands + C.Length</c> bands. Mirrors libvips
/// <c>vips_bandjoin_const</c>.
///
/// <para>Common use: synthesize an alpha channel without going through
/// the full <see cref="VipsAddAlpha"/> path, or fold per-band constants
/// into a multi-band image for downstream <c>Linear</c>/<c>Recomb</c>
/// math.</para>
/// </summary>
public class VipsBandjoinConst : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public double[]? C { get; set; }

    public override int Build()
    {
        if (In == null || C == null || C.Length == 0) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = In.Bands + C.Length,
            BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = C,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("BandjoinConst"); h.Add(RuntimeHelpers.GetHashCode(In));
        if (C != null) foreach (var v in C) h.Add(v);
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        var c = (double[])b!;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        VipsImage @in = inRegion.Image;
        bool isFloat = @in.BandFormat == VipsBandFormat.Float;
        int sampleSize = isFloat ? 4 : 1;
        int inBands = @in.Bands;
        int extraBands = c.Length;
        int outBands = inBands + extraBands;
        int inPelBytes = inBands * sampleSize;
        int outPelBytes = outBands * sampleSize;
        int extraBytes = extraBands * sampleSize;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                inAddr.Slice(x * inPelBytes, inPelBytes)
                      .CopyTo(outAddr.Slice(x * outPelBytes, inPelBytes));
                if (isFloat)
                {
                    for (int i = 0; i < extraBands; i++)
                        BinaryPrimitives.WriteSingleLittleEndian(
                            outAddr.Slice(x * outPelBytes + inPelBytes + i * 4, 4), (float)c[i]);
                }
                else
                {
                    for (int i = 0; i < extraBands; i++)
                        outAddr[x * outPelBytes + inPelBytes + i] = (byte)Math.Clamp(c[i], 0, 255);
                }
            }
        }
        return 0;
    }
}
