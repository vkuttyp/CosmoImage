using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Convolution;

/// <summary>
/// Spatial normalised cross-correlation. For each output pixel, slides
/// the <see cref="Reference"/> template over the input and computes
/// Pearson's correlation coefficient between the input window and the
/// template:
/// <code>
/// out(x, y) = Σ (in_w − μ_w)(ref − μ_r) /
///             sqrt(Σ (in_w − μ_w)² · Σ (ref − μ_r)²)
/// </code>
/// <para>The result is a real value in <c>[-1, 1]</c>; we map it to
/// <c>0..255</c> UChar for storage. Mirrors libvips <c>vips_spcor</c>.
/// UChar 1-band only.</para>
///
/// <para>Useful for QR-marker localisation, image-registration
/// alignment, and template-driven object detection. Output is sized
/// <c>(W − tw + 1, H − th + 1)</c> — a template doesn't fit at the
/// last <c>tw − 1</c> columns or <c>th − 1</c> rows.</para>
/// </summary>
public class VipsSpcor : VipsOperation
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

        // Materialise both. Spcor needs random access into the input.
        byte[] inPixels = MaterialiseUChar(In);
        byte[] refPixels = MaterialiseUChar(Reference);

        int outW = In.Width - Reference.Width + 1;
        int outH = In.Height - Reference.Height + 1;

        // Pre-compute reference centred values + ||ref − μ||.
        int tw = Reference.Width, th = Reference.Height;
        long sumRef = 0;
        for (int i = 0; i < refPixels.Length; i++) sumRef += refPixels[i];
        double meanRef = (double)sumRef / (tw * th);
        var refCentred = new double[tw * th];
        double normRefSq = 0;
        for (int i = 0; i < refPixels.Length; i++)
        {
            double d = refPixels[i] - meanRef;
            refCentred[i] = d;
            normRefSq += d * d;
        }
        double normRef = Math.Sqrt(normRefSq);

        var output = new byte[outW * outH];
        for (int y = 0; y < outH; y++)
        {
            for (int x = 0; x < outW; x++)
            {
                long sumIn = 0;
                for (int wy = 0; wy < th; wy++)
                {
                    int rowBase = (y + wy) * In.Width + x;
                    for (int wx = 0; wx < tw; wx++) sumIn += inPixels[rowBase + wx];
                }
                double meanIn = (double)sumIn / (tw * th);

                double dot = 0;
                double normInSq = 0;
                for (int wy = 0; wy < th; wy++)
                {
                    int rowBase = (y + wy) * In.Width + x;
                    int refRowBase = wy * tw;
                    for (int wx = 0; wx < tw; wx++)
                    {
                        double dIn = inPixels[rowBase + wx] - meanIn;
                        dot += dIn * refCentred[refRowBase + wx];
                        normInSq += dIn * dIn;
                    }
                }
                double normIn = Math.Sqrt(normInSq);
                double r = (normIn > 0 && normRef > 0) ? dot / (normIn * normRef) : 0;
                // Map [-1, 1] → [0, 255].
                output[y * outW + x] = (byte)Math.Clamp((r + 1.0) * 127.5, 0, 255);
            }
        }

        Out = new VipsImage
        {
            Width = outW, Height = outH, Bands = 1,
            BandFormat = VipsBandFormat.UChar,
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
        => HashCode.Combine("Spcor", RuntimeHelpers.GetHashCode(In), RuntimeHelpers.GetHashCode(Reference));

    private static byte[] MaterialiseUChar(VipsImage img)
    {
        if (img.Pixels is { } existing) return existing;
        var sink = new MemorySink(img);
        sink.RunAsync().GetAwaiter().GetResult();
        return sink.Pixels;
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var output = (byte[])b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        int W = @out.Width;
        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            int srcRow = (r.Top + y) * W + r.Left;
            output.AsSpan(srcRow, r.Width).CopyTo(outAddr);
        }
        return 0;
    }
}
