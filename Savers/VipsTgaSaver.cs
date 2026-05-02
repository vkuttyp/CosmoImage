using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Core;

namespace CosmoImage.Savers;

/// <summary>
/// TGA writer. Pure-C# emitter for the modern uncompressed variants:
/// type 2 (RGB, 24/32 bpp BGR/BGRA) and type 3 (grayscale, 8 bpp).
/// Output uses top-to-bottom row order via image-descriptor bit 5 — no
/// row flip needed and most viewers prefer it.
///
/// <para>RLE-compressed output (types 10/11) is intentionally not emitted
/// because the savings are negligible on photographic data and modern
/// pipelines don't need TGA's tiny RLE scheme — callers wanting tight
/// lossless compression should use PNG or QOI instead.</para>
/// </summary>
public static class VipsTgaSaver
{
    public static async Task SaveAsync(VipsImage image, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (image.Bands != 1 && image.Bands != 3 && image.Bands != 4)
            throw new NotSupportedException($"TGA save needs 1, 3, or 4 bands; got {image.Bands}");

        var src = image.BandFormat == VipsBandFormat.UChar
            ? image
            : VipsImageOps.CastUChar(image);

        byte[] pixels;
        if (src.Pixels is { } existing)
        {
            pixels = existing;
        }
        else
        {
            var sink = new MemorySink(src);
            await sink.RunAsync(cancellationToken);
            pixels = sink.Pixels;
        }

        int W = src.Width;
        int H = src.Height;
        int bands = src.Bands;
        if (W > ushort.MaxValue || H > ushort.MaxValue)
            throw new NotSupportedException($"TGA dimensions are 16-bit; got {W}×{H}");

        byte imageType = bands == 1 ? (byte)3 : (byte)2; // 3 = grayscale, 2 = RGB
        byte depth = bands switch { 1 => 8, 3 => 24, _ => 32 };
        // Image descriptor: bit 5 set = top-to-bottom; alpha bits in low nibble.
        byte descriptor = (byte)((bands == 4 ? 8 : 0) | 0x20);

        var stream = writer.AsStream();

        var header = new byte[18];
        header[0] = 0;            // ID length
        header[1] = 0;            // colour-map type (none)
        header[2] = imageType;
        // bytes 3..7 colour-map specification — all zero for non-paletted
        // bytes 8..11 x/y origin — zero
        header[12] = (byte)(W & 0xFF); header[13] = (byte)((W >> 8) & 0xFF);
        header[14] = (byte)(H & 0xFF); header[15] = (byte)((H >> 8) & 0xFF);
        header[16] = depth;
        header[17] = descriptor;
        await stream.WriteAsync(header, cancellationToken);

        // Pixel data, top-to-bottom (per descriptor bit), BGR(A) byte order.
        var rowBuffer = new byte[W * bands];
        for (int y = 0; y < H; y++)
        {
            int srcOffset = y * W * bands;
            if (bands == 1)
            {
                Buffer.BlockCopy(pixels, srcOffset, rowBuffer, 0, W);
            }
            else
            {
                for (int x = 0; x < W; x++)
                {
                    int sp = srcOffset + x * bands;
                    int dp = x * bands;
                    rowBuffer[dp + 0] = pixels[sp + 2]; // B from R-pos
                    rowBuffer[dp + 1] = pixels[sp + 1];
                    rowBuffer[dp + 2] = pixels[sp + 0]; // R from B-pos
                    if (bands == 4) rowBuffer[dp + 3] = pixels[sp + 3];
                }
            }
            await stream.WriteAsync(rowBuffer, cancellationToken);
        }

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }
}
