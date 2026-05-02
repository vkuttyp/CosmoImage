using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using MathNet.Numerics.IntegralTransforms;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Inverse 2D FFT. Takes a DPComplex frequency-domain image (typically the
/// output of <see cref="VipsFwFft"/>) and emits a UChar spatial-domain image
/// using the magnitude of the inverse transform, clamped. Round-trip
/// FwFft → InvFft reconstructs the original within floating-point error.
/// Mirrors libvips <c>vips_invfft</c>.
/// </summary>
public class VipsInvFft : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.DPComplex) return -1;

        Out = new VipsImage
        {
            Width = In.Width,
            Height = In.Height,
            Bands = In.Bands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            XRes = 1.0,
            YRes = 1.0,
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("InvFft", RuntimeHelpers.GetHashCode(In));

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        VipsRect r = outRegion.Valid;

        int width = @in.Width;
        int height = @in.Height;
        int bands = @in.Bands;
        int inPel = @in.SizeOfPel;

        inRegion.Prepare(new VipsRect(0, 0, width, height));

        for (int bnd = 0; bnd < bands; bnd++)
        {
            var data = new Complex[width * height];
            for (int y = 0; y < height; y++)
            {
                var line = inRegion.GetAddress(0, y);
                for (int x = 0; x < width; x++)
                {
                    int baseOff = x * inPel + bnd * 16;
                    double re = BitConverter.ToDouble(line.Slice(baseOff, 8));
                    double im = BitConverter.ToDouble(line.Slice(baseOff + 8, 8));
                    data[y * width + x] = new Complex(re, im);
                }
            }

            VipsFwFft.Inverse2DAsRowsCols(data, height, width);

            for (int y = 0; y < r.Height; y++)
            {
                var dest = outRegion.GetAddress(r.Left, r.Top + y);
                for (int x = 0; x < r.Width; x++)
                {
                    var c = data[(r.Top + y) * width + (r.Left + x)];
                    dest[x * bands + bnd] = (byte)Math.Clamp(c.Magnitude, 0, 255);
                }
            }
        }
        return 0;
    }
}

/// <summary>
/// Log-magnitude spectrum of a DPComplex frequency-domain image. Useful for
/// visualizing FFT output. Returns UChar; the DC component is centered (FFT
/// shift) so low frequencies sit in the middle of the image.
/// Mirrors libvips <c>vips_spectrum</c>.
/// </summary>
public class VipsSpectrum : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.DPComplex) return -1;

        Out = new VipsImage
        {
            Width = In.Width,
            Height = In.Height,
            Bands = In.Bands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            XRes = 1.0,
            YRes = 1.0,
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Spectrum", RuntimeHelpers.GetHashCode(In));

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        VipsRect r = outRegion.Valid;

        int width = @in.Width;
        int height = @in.Height;
        int bands = @in.Bands;
        int inPel = @in.SizeOfPel;

        inRegion.Prepare(new VipsRect(0, 0, width, height));

        // Two-pass: first compute log-magnitude into a float buffer per band,
        // find max for normalization, then write UChar with FFT-shift.
        var mags = new double[bands * width * height];
        var maxes = new double[bands];

        for (int bnd = 0; bnd < bands; bnd++)
        {
            double max = 0;
            for (int y = 0; y < height; y++)
            {
                var line = inRegion.GetAddress(0, y);
                for (int x = 0; x < width; x++)
                {
                    int baseOff = x * inPel + bnd * 16;
                    double re = BitConverter.ToDouble(line.Slice(baseOff, 8));
                    double im = BitConverter.ToDouble(line.Slice(baseOff + 8, 8));
                    double m = Math.Log(1 + Math.Sqrt(re * re + im * im));
                    mags[bnd * width * height + y * width + x] = m;
                    if (m > max) max = m;
                }
            }
            maxes[bnd] = max > 0 ? max : 1;
        }

        int cx = width / 2;
        int cy = height / 2;

        for (int y = 0; y < r.Height; y++)
        {
            var dest = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int gx = (r.Left + x + cx) % width;
                int gy = (r.Top + y + cy) % height;
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    double m = mags[bnd * width * height + gy * width + gx];
                    dest[x * bands + bnd] = (byte)Math.Clamp(m / maxes[bnd] * 255, 0, 255);
                }
            }
        }
        return 0;
    }
}
