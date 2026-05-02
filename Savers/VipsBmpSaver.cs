using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Core;

namespace CosmoImage.Savers;

/// <summary>
/// BMP writer. Pure-C# emitter for 24bpp BGR (3-band input) and 32bpp BGRA
/// (4-band input). 1-band grayscale is replicated to 24bpp BGR. Output is
/// always BITMAPINFOHEADER (40-byte DIB) with BI_RGB compression and
/// bottom-up row order — the layout every BMP reader on the planet handles.
///
/// <para>Variants intentionally not supported: paletted (1/4/8 bpp), 16bpp
/// RGB555, RLE compression, BITFIELDS, V4/V5 colour-space headers. Callers
/// who need those should use a Magick-backed format (PNG, TIFF) instead —
/// this saver covers the common interop case where a BMP is just a
/// container for raw pixels.</para>
/// </summary>
public static class VipsBmpSaver
{
    public static async Task SaveAsync(VipsImage image, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (image.Bands != 1 && image.Bands != 3 && image.Bands != 4)
            throw new NotSupportedException($"BMP save needs 1, 3, or 4 bands; got {image.Bands}");

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
        int srcBands = src.Bands;
        int outBpp = srcBands == 4 ? 32 : 24;
        int outBytesPerPixel = outBpp / 8;
        int outRowStride = ((W * outBpp + 31) / 32) * 4;
        int pixelDataSize = outRowStride * H;
        const int fileHeaderSize = 14;
        const int dibHeaderSize = 40;
        int pixelOffset = fileHeaderSize + dibHeaderSize;
        int fileSize = pixelOffset + pixelDataSize;

        var stream = writer.AsStream();

        // ---- BITMAPFILEHEADER (14 bytes) ----
        Span<byte> fileHeader = stackalloc byte[fileHeaderSize];
        fileHeader[0] = (byte)'B';
        fileHeader[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(fileHeader.Slice(2, 4), (uint)fileSize);
        // bytes[6..9] reserved, leave zero
        BinaryPrimitives.WriteUInt32LittleEndian(fileHeader.Slice(10, 4), (uint)pixelOffset);
        await stream.WriteAsync(fileHeader.ToArray(), cancellationToken);

        // ---- BITMAPINFOHEADER (40 bytes) ----
        Span<byte> dibHeader = stackalloc byte[dibHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(dibHeader.Slice(0, 4), dibHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(dibHeader.Slice(4, 4), W);
        // Positive height = rows stored bottom-up (the BMP default).
        BinaryPrimitives.WriteInt32LittleEndian(dibHeader.Slice(8, 4), H);
        BinaryPrimitives.WriteUInt16LittleEndian(dibHeader.Slice(12, 2), 1); // planes
        BinaryPrimitives.WriteUInt16LittleEndian(dibHeader.Slice(14, 2), (ushort)outBpp);
        BinaryPrimitives.WriteUInt32LittleEndian(dibHeader.Slice(16, 4), 0); // BI_RGB
        BinaryPrimitives.WriteUInt32LittleEndian(dibHeader.Slice(20, 4), (uint)pixelDataSize);
        // bytes[24..27] x-pixels-per-meter — leave zero
        // bytes[28..31] y-pixels-per-meter — leave zero
        // bytes[32..35] colors-used — leave zero (no palette)
        // bytes[36..39] colors-important — leave zero
        await stream.WriteAsync(dibHeader.ToArray(), cancellationToken);

        // ---- Pixel data, bottom-up, BGR(A) order ----
        var rowBuffer = new byte[outRowStride];
        for (int srcRow = H - 1; srcRow >= 0; srcRow--)
        {
            int srcOffset = srcRow * W * srcBands;
            for (int x = 0; x < W; x++)
            {
                int sp = srcOffset + x * srcBands;
                int dp = x * outBytesPerPixel;
                if (srcBands == 1)
                {
                    // Replicate gray to BGR.
                    byte g = pixels[sp];
                    rowBuffer[dp + 0] = g;
                    rowBuffer[dp + 1] = g;
                    rowBuffer[dp + 2] = g;
                }
                else
                {
                    // RGB(A) → BGR(A): swap R/B channels.
                    rowBuffer[dp + 0] = pixels[sp + 2];
                    rowBuffer[dp + 1] = pixels[sp + 1];
                    rowBuffer[dp + 2] = pixels[sp + 0];
                    if (outBpp == 32) rowBuffer[dp + 3] = pixels[sp + 3];
                }
            }
            // Padding bytes after the last pixel are already zero from
            // the buffer's initial state since we don't overwrite them.
            await stream.WriteAsync(rowBuffer, cancellationToken);
        }

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }
}
