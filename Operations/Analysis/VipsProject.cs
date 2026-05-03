using System;
using System.Buffers.Binary;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Per-axis sum reduction. <c>columns</c> is a 1-row image where each
/// pixel holds the sum down that column of the input;
/// <c>rows</c> is a 1-column image where each pixel holds the sum
/// across that row. Mirrors libvips <c>vips_project</c>.
///
/// <para>Output is Float (preserves accuracy across many UChar
/// inputs); output band count matches the input. Useful for
/// document-image projection profiles, per-row exposure analysis,
/// and 1D histograms over an axis.</para>
/// </summary>
public static class VipsProject
{
    public static (VipsImage Columns, VipsImage Rows) Compute(VipsImage input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (input.BandFormat != VipsBandFormat.UChar && input.BandFormat != VipsBandFormat.Float)
            throw new ArgumentException("Project requires UChar or Float input.", nameof(input));

        int W = input.Width, H = input.Height, bands = input.Bands;

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

        // Column sums (per column, sum down all rows). Output is (W, 1).
        var colBytes = new byte[W * bands * 4];
        for (int x = 0; x < W; x++)
        {
            for (int bnd = 0; bnd < bands; bnd++)
            {
                double sum = 0;
                for (int y = 0; y < H; y++)
                {
                    int off = (y * W + x) * pelBytes + bnd * sampleSize;
                    sum += isFloat
                        ? BinaryPrimitives.ReadSingleLittleEndian(pixels.AsSpan(off, 4))
                        : pixels[off];
                }
                BinaryPrimitives.WriteSingleLittleEndian(
                    colBytes.AsSpan((x * bands + bnd) * 4, 4), (float)sum);
            }
        }

        // Row sums (per row, sum across all columns). Output is (1, H).
        var rowBytes = new byte[H * bands * 4];
        for (int y = 0; y < H; y++)
        {
            for (int bnd = 0; bnd < bands; bnd++)
            {
                double sum = 0;
                for (int x = 0; x < W; x++)
                {
                    int off = (y * W + x) * pelBytes + bnd * sampleSize;
                    sum += isFloat
                        ? BinaryPrimitives.ReadSingleLittleEndian(pixels.AsSpan(off, 4))
                        : pixels[off];
                }
                BinaryPrimitives.WriteSingleLittleEndian(
                    rowBytes.AsSpan((y * bands + bnd) * 4, 4), (float)sum);
            }
        }

        var columns = MakeBufferImage(W, 1, bands, colBytes, input);
        var rows = MakeBufferImage(1, H, bands, rowBytes, input);
        return (columns, rows);
    }

    private static VipsImage MakeBufferImage(int w, int h, int bands, byte[] buf, VipsImage src)
    {
        return new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.Histogram,
            Coding = src.Coding, XRes = 1, YRes = 1,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                VipsRect r = reg.Valid;
                for (int yy = 0; yy < r.Height; yy++)
                {
                    var addr = reg.GetAddress(r.Left, r.Top + yy);
                    int srcOff = ((r.Top + yy) * w + r.Left) * bands * 4;
                    buf.AsSpan(srcOff, r.Width * bands * 4).CopyTo(addr);
                }
                return 0;
            },
        };
    }
}
