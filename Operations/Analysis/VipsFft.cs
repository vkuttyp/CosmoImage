using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using MathNet.Numerics.IntegralTransforms;

namespace CosmoImage.Operations.Analysis;

public class VipsFwFft : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;

        Out = new VipsImage
        {
            Width = In.Width,
            Height = In.Height,
            Bands = In.Bands,
            BandFormat = VipsBandFormat.DPComplex, // Double Precision Complex
            Interpretation = VipsInterpretation.Fourier,
            XRes = 1.0,
            YRes = 1.0,
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In
        };
        Out.SetPipeline(VipsDemandStyle.Any, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        return HashCode.Combine("FwFft", RuntimeHelpers.GetHashCode(In));
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        VipsRect r = outRegion.Valid;

        // FFT requires global knowledge.
        // For simplicity, we process the whole band if it's not already done.
        // In a real port, we'd cache this globally or use tiles if possible.

        int width = @in.Width;
        int height = @in.Height;
        int bands = @in.Bands;

        inRegion.Prepare(new VipsRect(0, 0, width, height));

        for (int bnd = 0; bnd < bands; bnd++)
        {
            Complex[] data = new Complex[width * height];
            for (int y = 0; y < height; y++)
            {
                var line = inRegion.GetAddress(0, y);
                for (int x = 0; x < width; x++)
                {
                    data[y * width + x] = new Complex(line[x * @in.SizeOfPel + bnd], 0);
                }
            }

            // 2D FFT as two 1D passes. The managed MathNet provider implements
            // Forward(row) reliably; Forward2D throws NotSupported in some
            // builds, so we drive it ourselves: rows first, then columns.
            Forward2DAsRowsCols(data, height, width);

            // Write back to outRegion (if requested region matches)
            // Note: This implementation assumes outRegion is the full image for now
            // since FFT is global.
            for (int y = 0; y < r.Height; y++)
            {
                var destLine = outRegion.GetAddress(r.Left, r.Top + y);
                for (int x = 0; x < r.Width; x++)
                {
                    var c = data[(r.Top + y) * width + (r.Left + x)];
                    // DPComplex is 16 bytes (2 doubles)
                    BitConverter.TryWriteBytes(destLine.Slice(x * 16 + bnd * 16, 8), c.Real);
                    BitConverter.TryWriteBytes(destLine.Slice(x * 16 + bnd * 16 + 8, 8), c.Imaginary);
                }
            }
        }

        return 0;
    }

    internal static void Forward2DAsRowsCols(Complex[] data, int rows, int cols)
    {
        var rowBuf = new Complex[cols];
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++) rowBuf[x] = data[y * cols + x];
            Fourier.Forward(rowBuf, FourierOptions.Default);
            for (int x = 0; x < cols; x++) data[y * cols + x] = rowBuf[x];
        }
        var colBuf = new Complex[rows];
        for (int x = 0; x < cols; x++)
        {
            for (int y = 0; y < rows; y++) colBuf[y] = data[y * cols + x];
            Fourier.Forward(colBuf, FourierOptions.Default);
            for (int y = 0; y < rows; y++) data[y * cols + x] = colBuf[y];
        }
    }

    internal static void Inverse2DAsRowsCols(Complex[] data, int rows, int cols)
    {
        var rowBuf = new Complex[cols];
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++) rowBuf[x] = data[y * cols + x];
            Fourier.Inverse(rowBuf, FourierOptions.Default);
            for (int x = 0; x < cols; x++) data[y * cols + x] = rowBuf[x];
        }
        var colBuf = new Complex[rows];
        for (int x = 0; x < cols; x++)
        {
            for (int y = 0; y < rows; y++) colBuf[y] = data[y * cols + x];
            Fourier.Inverse(colBuf, FourierOptions.Default);
            for (int y = 0; y < rows; y++) data[y * cols + x] = colBuf[y];
        }
    }
}
