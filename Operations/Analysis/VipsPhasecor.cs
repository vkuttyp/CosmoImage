using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Phase correlation between two equal-sized single-band UChar images.
/// Mirrors libvips <c>vips_phasecor</c>.
///
/// <para>Math: <c>P = FFT(a) · conj(FFT(b)) / |FFT(a) · conj(FFT(b))|</c>,
/// then <c>IFFT(P)</c>. The output Float image has a single sharp peak
/// at the <c>(Δx, Δy)</c> that best aligns <see cref="In1"/> with
/// <see cref="In2"/> — useful for image registration / motion estimation.
/// Peak coordinate is modulo <c>(W, H)</c>: a shift of <c>(W-1, 0)</c>
/// reads as <c>(-1, 0)</c>.</para>
///
/// <para>Whitening (the <c>/ |…|</c> step) gives translation-invariant
/// matching that is robust to brightness / contrast differences;
/// trade-off is sensitivity to noise and high-frequency content. For
/// pure cross-correlation use <c>spcor</c> (spatial) or the planned
/// <c>fastcor</c> (FFT-based, no whitening).</para>
/// </summary>
public class VipsPhasecor : VipsOperation
{
    public VipsImage? In1 { get; set; }
    public VipsImage? In2 { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In1 == null || In2 == null) return -1;
        if (In1.Width != In2.Width || In1.Height != In2.Height) return -1;
        if (In1.Bands != 1 || In2.Bands != 1) return -1;
        if (In1.BandFormat != VipsBandFormat.UChar || In2.BandFormat != VipsBandFormat.UChar) return -1;

        Out = new VipsImage
        {
            Width = In1.Width,
            Height = In1.Height,
            Bands = 1,
            BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.BW,
            XRes = 1.0,
            YRes = 1.0,
            StartFn = VipsSeq.StartMany,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopMany,
            ClientA = new[] { In1, In2 },
        };
        Out.SetPipeline(VipsDemandStyle.Any, In1, In2);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Phasecor",
            RuntimeHelpers.GetHashCode(In1),
            RuntimeHelpers.GetHashCode(In2));

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var r1 = regions[0];
        var r2 = regions[1];
        VipsImage img = r1.Image;
        int width = img.Width, height = img.Height;
        VipsRect r = outRegion.Valid;

        // The phase-correlation formula needs both whole inputs. Prepare
        // the full extent of each then do the FFTs once.
        if (r1.Prepare(new VipsRect(0, 0, width, height)) != 0) return -1;
        if (r2.Prepare(new VipsRect(0, 0, width, height)) != 0) return -1;

        var aBuf = new Complex[width * height];
        var bBuf = new Complex[width * height];
        for (int y = 0; y < height; y++)
        {
            var aLine = r1.GetAddress(0, y);
            var bLine = r2.GetAddress(0, y);
            for (int x = 0; x < width; x++)
            {
                aBuf[y * width + x] = new Complex(aLine[x] / 255.0, 0);
                bBuf[y * width + x] = new Complex(bLine[x] / 255.0, 0);
            }
        }

        VipsFwFft.Forward2DAsRowsCols(aBuf, height, width);
        VipsFwFft.Forward2DAsRowsCols(bBuf, height, width);

        // Cross-power spectrum, whitened: P = A · conj(B) / |A · conj(B)|.
        for (int i = 0; i < aBuf.Length; i++)
        {
            Complex cross = aBuf[i] * Complex.Conjugate(bBuf[i]);
            double mag = cross.Magnitude;
            // Avoid divide-by-zero on bins where the cross-spectrum
            // collapses (happens for constant images / zero high frequencies).
            aBuf[i] = mag > 1e-12 ? cross / mag : Complex.Zero;
        }

        VipsFwFft.Inverse2DAsRowsCols(aBuf, height, width);

        for (int y = 0; y < r.Height; y++)
        {
            var dest = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                var c = aBuf[(r.Top + y) * width + (r.Left + x)];
                BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(x * 4, 4), (float)c.Real);
            }
        }
        return 0;
    }
}
