using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using CosmoImage.Operations.Analysis;

namespace CosmoImage.Operations.Convolution;

/// <summary>
/// FFT-accelerated cross-correlation of an input image with a reference
/// template. Mirrors libvips <c>vips_fastcor</c>.
///
/// <para>For each output pixel <c>(x, y)</c> in the valid region,
/// computes <c>Σ in(x+u, y+v) · ref(u, v)</c> over the template extent.
/// FFT-based: the spatial cross-correlation theorem says
/// <c>F{in ⋆ ref} = F{in} · conj(F{ref})</c>, so the algorithm is
/// FFT-twice → element-wise multiply → IFFT once. Cost is
/// <c>O(W·H·log(W·H))</c> instead of <c>O(W·H·tw·th)</c> for spatial,
/// a big win once the template gets larger than ~10×10.</para>
///
/// <para>Output: UInt 1-band, sized <c>(W − tw + 1, H − th + 1)</c>.
/// Values are raw summed-products — not normalized — so brightness or
/// contrast changes between input and template will skew the peak. For
/// brightness-invariant matching, use <see cref="VipsSpcor"/> (Pearson
/// NCC, slower) or <see cref="Operations.Analysis.VipsPhasecor"/> (whitened cross-spectrum,
/// translation-only).</para>
/// </summary>
public class VipsFastcor : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Reference { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null || Reference == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;
        if (Reference.BandFormat != VipsBandFormat.UChar) return -1;
        if (In.Bands != 1 || Reference.Bands != 1) return -1;
        if (Reference.Width > In.Width || Reference.Height > In.Height) return -1;

        byte[] inPixels = MaterialiseUChar(In);
        byte[] refPixels = MaterialiseUChar(Reference);
        int W = In.Width, H = In.Height;
        int tw = Reference.Width, th = Reference.Height;
        int outW = W - tw + 1, outH = H - th + 1;

        // FFT-based cross-correlation. Pad reference into a W×H buffer
        // (reference at top-left, zeros elsewhere) and use circular FFT
        // — the valid output region [0..W-tw, 0..H-th] is wrap-free
        // because (x+u) ∈ [0..W-1] for u ∈ [0..tw-1], x ∈ [0..W-tw].
        var inBuf = new Complex[W * H];
        var refBuf = new Complex[W * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
                inBuf[y * W + x] = new Complex(inPixels[y * W + x], 0);
        }
        for (int y = 0; y < th; y++)
        {
            for (int x = 0; x < tw; x++)
                refBuf[y * W + x] = new Complex(refPixels[y * tw + x], 0);
        }

        VipsFwFft.Forward2DAsRowsCols(inBuf, H, W);
        VipsFwFft.Forward2DAsRowsCols(refBuf, H, W);

        for (int i = 0; i < inBuf.Length; i++)
            inBuf[i] *= Complex.Conjugate(refBuf[i]);

        VipsFwFft.Inverse2DAsRowsCols(inBuf, H, W);

        // MathNet's default FourierOptions uses symmetric scaling (1/√N on
        // forward AND inverse). Forward(in)·conj(Forward(ref))→Inverse(...)
        // therefore produces values 1/√(W·H) of the unscaled cross-
        // correlation. Restore the natural scaling so the output equals
        // Σ in·ref pixel-for-pixel.
        double scale = Math.Sqrt(W * H);
        var output = new uint[outW * outH];
        for (int y = 0; y < outH; y++)
        {
            for (int x = 0; x < outW; x++)
            {
                double v = inBuf[y * W + x].Real * scale;
                output[y * outW + x] = (uint)Math.Max(0, Math.Round(v));
            }
        }

        Out = new VipsImage
        {
            Width = outW, Height = outH, Bands = 1,
            BandFormat = VipsBandFormat.UInt,
            Interpretation = VipsInterpretation.BW,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = output,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Fastcor",
            RuntimeHelpers.GetHashCode(In),
            RuntimeHelpers.GetHashCode(Reference));

    private static byte[] MaterialiseUChar(VipsImage img)
    {
        if (img.Pixels is { } existing) return existing;
        var sink = new MemorySink(img);
        sink.RunAsync().GetAwaiter().GetResult();
        return sink.Pixels;
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var output = (uint[])b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        int W = @out.Width;
        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                uint v = output[(r.Top + y) * W + (r.Left + x)];
                BinaryPrimitives.WriteUInt32LittleEndian(outAddr.Slice(x * 4, 4), v);
            }
        }
        return 0;
    }
}
