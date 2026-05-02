using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Core;

namespace CosmoImage.Savers;

/// <summary>
/// QOI (Quite OK Image) writer. Pure-C# implementation against the v1.0
/// QOI spec. Picks the most compact tag per pixel by walking the same
/// state machine as the decoder: previous pixel and 64-entry hash table,
/// both reset at start.
///
/// <para>Encoding strategy: prefer RUN (≤ 62 identical pixels), then
/// INDEX (hash table hit), then DIFF (small per-channel deltas), then
/// LUMA (mid-range delta), then RGB / RGBA full pixel. RGBA is only
/// emitted when alpha changes; otherwise RGB suffices.</para>
///
/// Input must be 3 (RGB) or 4 (RGBA) bands UChar; other combinations
/// throw.
/// </summary>
public static class VipsQoiSaver
{
    public static async Task SaveAsync(VipsImage image, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (image.Bands != 3 && image.Bands != 4)
            throw new NotSupportedException($"QOI save needs 3 or 4 bands; got {image.Bands}");

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
        int channels = src.Bands;

        // Encode into a memory buffer first; QOI's tag stream length depends
        // on content so a single-pass write through PipeWriter would need
        // small flushes anyway. The intermediate buffer is bounded above by
        // 5*W*H + 14 + 8 (worst case: every pixel emits as RGB or RGBA).
        var maxSize = (long)W * H * (channels + 1) + 14 + 8;
        var buf = new byte[maxSize];
        int p = 0;

        // Header
        buf[p++] = (byte)'q'; buf[p++] = (byte)'o'; buf[p++] = (byte)'i'; buf[p++] = (byte)'f';
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p, 4), (uint)W); p += 4;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p, 4), (uint)H); p += 4;
        buf[p++] = (byte)channels;
        buf[p++] = 0; // sRGB with linear alpha — informational only

        byte pr = 0, pg = 0, pb = 0, pa = 255;
        var idxR = new byte[64];
        var idxG = new byte[64];
        var idxB = new byte[64];
        var idxA = new byte[64];

        int run = 0;
        long pxCount = (long)W * H;
        for (long i = 0; i < pxCount; i++)
        {
            int srcOff = (int)(i * channels);
            byte r = pixels[srcOff];
            byte g = pixels[srcOff + 1];
            byte b = pixels[srcOff + 2];
            byte a = channels == 4 ? pixels[srcOff + 3] : pa;

            if (r == pr && g == pg && b == pb && a == pa)
            {
                run++;
                if (run == 62 || i == pxCount - 1)
                {
                    buf[p++] = (byte)(0xC0 | (run - 1));
                    run = 0;
                }
            }
            else
            {
                if (run > 0)
                {
                    buf[p++] = (byte)(0xC0 | (run - 1));
                    run = 0;
                }

                int hi = (r * 3 + g * 5 + b * 7 + a * 11) & 0x3F;
                if (idxR[hi] == r && idxG[hi] == g && idxB[hi] == b && idxA[hi] == a)
                {
                    // INDEX hit
                    buf[p++] = (byte)hi; // top 2 bits = 00
                }
                else
                {
                    idxR[hi] = r; idxG[hi] = g; idxB[hi] = b; idxA[hi] = a;

                    if (a == pa)
                    {
                        // Compute deltas against previous pixel.
                        int dr = (sbyte)(r - pr);
                        int dg = (sbyte)(g - pg);
                        int db = (sbyte)(b - pb);
                        int drDg = dr - dg;
                        int dbDg = db - dg;

                        if (dr >= -2 && dr <= 1 && dg >= -2 && dg <= 1 && db >= -2 && db <= 1)
                        {
                            // QOI_OP_DIFF
                            buf[p++] = (byte)(0x40 | ((dr + 2) << 4) | ((dg + 2) << 2) | (db + 2));
                        }
                        else if (drDg >= -8 && drDg <= 7 && dg >= -32 && dg <= 31 && dbDg >= -8 && dbDg <= 7)
                        {
                            // QOI_OP_LUMA
                            buf[p++] = (byte)(0x80 | (dg + 32));
                            buf[p++] = (byte)(((drDg + 8) << 4) | (dbDg + 8));
                        }
                        else
                        {
                            // QOI_OP_RGB
                            buf[p++] = 0xFE;
                            buf[p++] = r; buf[p++] = g; buf[p++] = b;
                        }
                    }
                    else
                    {
                        // QOI_OP_RGBA
                        buf[p++] = 0xFF;
                        buf[p++] = r; buf[p++] = g; buf[p++] = b; buf[p++] = a;
                    }
                }
            }

            pr = r; pg = g; pb = b; pa = a;
        }

        // End marker: 7 zero bytes + 0x01.
        buf[p++] = 0; buf[p++] = 0; buf[p++] = 0; buf[p++] = 0;
        buf[p++] = 0; buf[p++] = 0; buf[p++] = 0; buf[p++] = 1;

        var stream = writer.AsStream();
        await stream.WriteAsync(buf.AsMemory(0, p), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }
}
