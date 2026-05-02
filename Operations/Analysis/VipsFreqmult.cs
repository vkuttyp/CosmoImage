using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Frequency-domain multiply: <c>FwFft → multiply by mask → InvFft</c>.
/// The mask is a real Float image of the same dimensions as
/// <see cref="In"/>; each complex frequency-domain sample is scaled by
/// the corresponding mask sample. Mirrors libvips <c>vips_freqmult</c>.
///
/// <para>Convenience wrapper for designed filters: build a mask in
/// frequency space (low-pass disk, Gaussian, etc.) and apply it to a
/// spatial-domain image in one step. Float input/output throughout —
/// users who started in UChar should <c>Cast(Float)</c> first.</para>
///
/// <para>Unlike <c>InvFft</c> which clamps the inverse magnitude to
/// UChar, Freqmult preserves the real part as Float so designed
/// filters round-trip cleanly.</para>
/// </summary>
public class VipsFreqmult : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Mask { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null || Mask == null) return -1;
        if (In.Bands != 1 || Mask.Bands != 1) return -1;
        if (In.BandFormat != VipsBandFormat.Float || Mask.BandFormat != VipsBandFormat.Float) return -1;
        if (In.Width != Mask.Width || In.Height != Mask.Height) return -1;

        int W = In.Width, H = In.Height;

        // Materialise both inputs.
        byte[] inPixels = MaterialiseFloat(In);
        byte[] maskPixels = MaterialiseFloat(Mask);

        // Build complex array from real input.
        var data = new Complex[W * H];
        for (int i = 0; i < W * H; i++)
            data[i] = new Complex(BinaryPrimitives.ReadSingleLittleEndian(inPixels.AsSpan(i * 4, 4)), 0);

        // Forward 2D FFT in place.
        VipsFwFft.Forward2DAsRowsCols(data, H, W);

        // Multiply by real mask.
        for (int i = 0; i < W * H; i++)
        {
            float m = BinaryPrimitives.ReadSingleLittleEndian(maskPixels.AsSpan(i * 4, 4));
            data[i] *= m;
        }

        // Inverse 2D FFT.
        VipsFwFft.Inverse2DAsRowsCols(data, H, W);

        // Write the real part to a Float buffer.
        var outBytes = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
            BinaryPrimitives.WriteSingleLittleEndian(outBytes.AsSpan(i * 4, 4), (float)data[i].Real);

        Out = new VipsImage
        {
            Width = W, Height = H, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = outBytes,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Freqmult", RuntimeHelpers.GetHashCode(In), RuntimeHelpers.GetHashCode(Mask));

    private static byte[] MaterialiseFloat(VipsImage img)
    {
        if (img.Pixels is { } existing) return existing;
        var sink = new MemorySink(img);
        sink.RunAsync().GetAwaiter().GetResult();
        return sink.Pixels;
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var outBytes = (byte[])b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        int W = @out.Width;
        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            int srcOff = ((r.Top + y) * W + r.Left) * 4;
            outBytes.AsSpan(srcOff, r.Width * 4).CopyTo(outAddr);
        }
        return 0;
    }
}
