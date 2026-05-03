using System;
using System.Buffers.Binary;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Sample patch averages from a regular grid. Divides
/// (<see cref="Left"/>, <see cref="Top"/>, <see cref="Width"/>,
/// <see cref="Height"/>) into <see cref="H"/> × <see cref="V"/>
/// rectangular patches; for each patch, returns the mean across the
/// <em>middle 80%</em> of the patch (avoiding edge bleed). Mirrors
/// libvips <c>vips_measure</c>.
///
/// <para>The standard colour-chart calibration primitive: photograph
/// a 6×4 ColorChecker, point Measure at the patch grid, get back a
/// 24-row matrix of (R, G, B) means ready for whitebalance / profile
/// fitting.</para>
///
/// <para>Output is a (<see cref="V"/>·<see cref="H"/>, 1) Float
/// matrix-style image with one row per patch (row-major across the
/// grid: row 0 = top-left, row H-1 = top-right, row H = second row's
/// leftmost, etc.). Bands match the input.</para>
/// </summary>
public static class VipsMeasure
{
    public static VipsImage Compute(VipsImage input,
        int h, int v,
        int left = 0, int top = 0, int width = 0, int height = 0)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (h < 1 || v < 1) throw new ArgumentException("h and v must be ≥ 1");

        int W = input.Width, H = input.Height, bands = input.Bands;
        if (width == 0) width = W - left;
        if (height == 0) height = H - top;
        if (left < 0 || top < 0 || left + width > W || top + height > H)
            throw new ArgumentException("measure rectangle exceeds image bounds");

        // Materialise input.
        byte[] pixels;
        if (input.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(input);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        bool isFloat = input.BandFormat == VipsBandFormat.Float;
        int sampleSize = isFloat ? 4 : 1;
        int pelBytes = bands * sampleSize;

        int patchCount = h * v;
        var output = new byte[patchCount * bands * 4];

        // Per-cell extents.
        for (int row = 0; row < v; row++)
        {
            int y0 = top + (row * height) / v;
            int y1 = top + ((row + 1) * height) / v;
            // Inset by 10% to avoid edge bleed.
            int yPad = (y1 - y0) / 10;
            int yi0 = y0 + yPad, yi1 = y1 - yPad;

            for (int col = 0; col < h; col++)
            {
                int x0 = left + (col * width) / h;
                int x1 = left + ((col + 1) * width) / h;
                int xPad = (x1 - x0) / 10;
                int xi0 = x0 + xPad, xi1 = x1 - xPad;

                int patchIdx = row * h + col;
                long count = (long)Math.Max(1, xi1 - xi0) * Math.Max(1, yi1 - yi0);
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    double sum = 0;
                    for (int yy = yi0; yy < yi1; yy++)
                    for (int xx = xi0; xx < xi1; xx++)
                    {
                        int off = (yy * W + xx) * pelBytes + bnd * sampleSize;
                        sum += isFloat
                            ? BinaryPrimitives.ReadSingleLittleEndian(pixels.AsSpan(off, 4))
                            : pixels[off];
                    }
                    BinaryPrimitives.WriteSingleLittleEndian(
                        output.AsSpan((patchIdx * bands + bnd) * 4, 4),
                        (float)(sum / count));
                }
            }
        }

        return new VipsImage
        {
            Width = patchCount, Height = 1, Bands = bands, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.Matrix,
            Coding = input.Coding, XRes = 1, YRes = 1,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                VipsRect r = reg.Valid;
                var addr = reg.GetAddress(r.Left, 0);
                int rowBytes = r.Width * bands * 4;
                int srcOff = r.Left * bands * 4;
                output.AsSpan(srcOff, rowBytes).CopyTo(addr);
                return 0;
            },
        };
    }
}
