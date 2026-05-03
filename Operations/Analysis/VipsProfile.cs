using System;
using System.Buffers.Binary;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// First non-zero pixel from each axis. Returns two single-band UInt
/// images:
/// <list type="bullet">
///   <item><c>Columns</c>: width=W, height=1. Pixel at column <c>x</c>
///     is the smallest <c>y</c> at which the input has a non-zero
///     value in that column. <c>H</c> if the column is all zero.</item>
///   <item><c>Rows</c>: width=1, height=H. Pixel at row <c>y</c> is
///     the smallest <c>x</c> at which the input has a non-zero value
///     in that row.</item>
/// </list>
///
/// <para>Mirrors libvips <c>vips_profile</c>. Common use is finding
/// the bounding rectangle of foreground content from each side, or
/// estimating a column-wise top profile (architectural drawings,
/// shadow analysis).</para>
/// </summary>
public static class VipsProfile
{
    public static (VipsImage Columns, VipsImage Rows) Compute(VipsImage input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (input.BandFormat != VipsBandFormat.UChar)
            throw new ArgumentException("Profile requires UChar input.", nameof(input));

        byte[] pixels;
        if (input.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(input);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        int W = input.Width, H = input.Height, bands = input.Bands;

        // Per-column first-non-zero y.
        var colBuf = new byte[W * 4];
        for (int x = 0; x < W; x++)
        {
            uint firstY = (uint)H;
            for (int y = 0; y < H; y++)
            {
                int pelOff = (y * W + x) * bands;
                bool any = false;
                for (int b = 0; b < bands; b++) if (pixels[pelOff + b] != 0) { any = true; break; }
                if (any) { firstY = (uint)y; break; }
            }
            BinaryPrimitives.WriteUInt32LittleEndian(colBuf.AsSpan(x * 4, 4), firstY);
        }

        // Per-row first-non-zero x.
        var rowBuf = new byte[H * 4];
        for (int y = 0; y < H; y++)
        {
            uint firstX = (uint)W;
            int rowBase = y * W;
            for (int x = 0; x < W; x++)
            {
                int pelOff = (rowBase + x) * bands;
                bool any = false;
                for (int b = 0; b < bands; b++) if (pixels[pelOff + b] != 0) { any = true; break; }
                if (any) { firstX = (uint)x; break; }
            }
            BinaryPrimitives.WriteUInt32LittleEndian(rowBuf.AsSpan(y * 4, 4), firstX);
        }

        var columns = MakeBuf(W, 1, colBuf);
        var rows = MakeBuf(1, H, rowBuf);
        return (columns, rows);
    }

    private static VipsImage MakeBuf(int w, int h, byte[] buf)
    {
        return new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UInt,
            Interpretation = VipsInterpretation.Histogram,
            XRes = 1, YRes = 1,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                VipsRect r = reg.Valid;
                for (int yy = 0; yy < r.Height; yy++)
                {
                    var addr = reg.GetAddress(r.Left, r.Top + yy);
                    int srcOff = ((r.Top + yy) * w + r.Left) * 4;
                    buf.AsSpan(srcOff, r.Width * 4).CopyTo(addr);
                }
                return 0;
            },
        };
    }
}
