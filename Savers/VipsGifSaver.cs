using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Operations.Misc;

namespace CosmoImage.Savers;

/// <summary>
/// GIF writer — pure-C#. Emits a single-frame or animated GIF89a stream
/// with per-frame Local Colour Tables (so multi-frame animations with
/// varied palettes don't lose fidelity to a forced global palette) and
/// LZW-compressed image data.
///
/// <para>Multi-frame layout uses the same convention as the
/// <see cref="VipsApngSaver"/> and <see cref="VipsWebpSaver"/>: image
/// height is <c>N · page-height</c>, with <c>n-pages</c>,
/// <c>page-height</c>, and optional <c>animation-delays</c> in
/// <see cref="VipsImage.Metadata"/>. <c>animation-delays</c> is in
/// 1/100-second units (matching the GIF GCE delay-time field).</para>
///
/// <para>Per-frame quantization runs each frame through the pure-C#
/// <see cref="VipsOctreeQuantizer"/> down to 256 colours and emits
/// the resulting palette as the frame's LCT. Animated GIFs loop
/// infinitely (NETSCAPE 2.0 app extension with loopCount=0).</para>
/// </summary>
public static class VipsGifSaver
{
    public static async Task SaveAsync(VipsImage image, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        int width = image.Width;
        int height = image.Height;
        int bands = image.Bands;

        if (bands != 1 && bands != 3 && bands != 4)
            throw new NotSupportedException($"GIF save needs 1, 3, or 4 bands; got {bands}");

        // Detect multi-frame layout. Default to single-frame.
        int nPages = 1;
        int pageHeight = height;
        if (image.Metadata.TryGetValue("n-pages", out var npStr) &&
            int.TryParse(npStr, out int nP) && nP > 0 &&
            image.Metadata.TryGetValue("page-height", out var phStr) &&
            int.TryParse(phStr, out int ph) && ph > 0 &&
            nP * ph == height)
        {
            nPages = nP;
            pageHeight = ph;
        }

        // Per-frame animation delay (1/100 sec units, matching GIF GCE field).
        // Default 10 = 100ms per frame when nothing is specified.
        var delays = new ushort[nPages];
        Array.Fill(delays, (ushort)10);
        if (image.Metadata.TryGetValue("animation-delays", out var dStr))
        {
            var parts = dStr.Split(',');
            for (int i = 0; i < Math.Min(parts.Length, nPages); i++)
                if (ushort.TryParse(parts[i], out var d)) delays[i] = d;
        }

        using var output = new MemoryStream();

        // ---- GIF89a header + Logical Screen Descriptor ----
        output.Write(System.Text.Encoding.ASCII.GetBytes("GIF89a"));

        Span<byte> lsd = stackalloc byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(lsd.Slice(0, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(lsd.Slice(2, 2), (ushort)pageHeight);
        // Packed: GCT-flag (0 — no global; per-frame LCT instead),
        // colour-resolution = 7 (8-bit channels), sort-flag = 0, GCT-size = 0.
        lsd[4] = 0b0111_0000;
        lsd[5] = 0; // background colour index — irrelevant without GCT
        lsd[6] = 0; // pixel aspect ratio — 0 = square
        output.Write(lsd);

        // ---- NETSCAPE 2.0 loop extension for animated GIFs ----
        if (nPages > 1)
        {
            // 21 FF 0B "NETSCAPE2.0" 03 01 LL LL 00
            output.WriteByte(0x21); output.WriteByte(0xFF); output.WriteByte(0x0B);
            output.Write(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            output.WriteByte(0x03);                     // sub-block size
            output.WriteByte(0x01);                     // sub-block ID
            output.WriteByte(0x00); output.WriteByte(0x00); // loop count = 0 (infinite)
            output.WriteByte(0x00);                     // block terminator
        }

        // ---- Per-frame data ----
        // Heap scratch reused across iterations. Span/stackalloc would
        // be cheaper but it can't cross the await boundaries inside
        // the per-frame loop.
        var idScratch = new byte[9];
        var delayScratch = new byte[2];
        for (int p = 0; p < nPages; p++)
        {
            // Slice this frame out of the stacked buffer and quantize.
            var frame = image.ExtractArea(0, p * pageHeight, width, pageHeight);
            var quantizer = new VipsOctreeQuantizer { Colors = 256 };
            var quantized = quantizer.Apply(frame);

            byte[] qPixels;
            if (quantized.Pixels is { } existingQ) qPixels = existingQ;
            else
            {
                var sink = new MemorySink(quantized);
                await sink.RunAsync(cancellationToken);
                qPixels = sink.Pixels;
            }

            // Build per-frame palette + indices in one pass. Keys pack
            // RGB into a uint so the dictionary lookup stays cheap.
            var paletteMap = new Dictionary<uint, int>();
            var paletteR = new List<byte>(256);
            var paletteG = new List<byte>(256);
            var paletteB = new List<byte>(256);
            var indices = new byte[width * pageHeight];

            for (int i = 0; i < width * pageHeight; i++)
            {
                byte r, g, b;
                int srcOff = i * bands;
                switch (bands)
                {
                    case 1: r = g = b = qPixels[srcOff]; break;
                    case 3: r = qPixels[srcOff]; g = qPixels[srcOff + 1]; b = qPixels[srcOff + 2]; break;
                    default: // 4 bands — drop alpha (GIF has 1-bit transparency, not full alpha)
                        r = qPixels[srcOff]; g = qPixels[srcOff + 1]; b = qPixels[srcOff + 2]; break;
                }
                uint key = ((uint)r << 16) | ((uint)g << 8) | b;
                if (!paletteMap.TryGetValue(key, out int idx))
                {
                    idx = paletteR.Count;
                    if (idx >= 256) throw new InvalidOperationException("Quantizer produced more than 256 distinct entries");
                    paletteMap[key] = idx;
                    paletteR.Add(r); paletteG.Add(g); paletteB.Add(b);
                }
                indices[i] = (byte)idx;
            }

            // GIF requires palette-table size to be a power of 2 (2..256).
            // Round paletteCount up; trailing entries get padded with zero.
            int paletteCount = paletteR.Count;
            int tableBits = Math.Max(1, (int)Math.Ceiling(Math.Log2(paletteCount)));
            int tableSize = 1 << tableBits;

            // ---- Graphic Control Extension (delay + dispose) ----
            output.WriteByte(0x21); output.WriteByte(0xF9); output.WriteByte(0x04);
            // Packed: reserved(3) | dispose(3) | userInput(1) | transparent(1)
            // Dispose = 2 (restore to background) for animations so the
            // canvas resets between frames.
            output.WriteByte((byte)((nPages > 1 ? 2 : 0) << 2));
            BinaryPrimitives.WriteUInt16LittleEndian(delayScratch.AsSpan(), delays[p]);
            output.Write(delayScratch);
            output.WriteByte(0); // transparent colour index — irrelevant
            output.WriteByte(0); // block terminator

            // ---- Image Descriptor ----
            output.WriteByte(0x2C);
            var idSpan = idScratch.AsSpan();
            BinaryPrimitives.WriteUInt16LittleEndian(idSpan.Slice(0, 2), 0); // left
            BinaryPrimitives.WriteUInt16LittleEndian(idSpan.Slice(2, 2), 0); // top
            BinaryPrimitives.WriteUInt16LittleEndian(idSpan.Slice(4, 2), (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(idSpan.Slice(6, 2), (ushort)pageHeight);
            // Packed: LCT-flag(1) | interlace(1) | sort(1) | reserved(2) | LCT-size(3)
            idScratch[8] = (byte)(0b1000_0000 | (tableBits - 1));
            output.Write(idScratch);

            // ---- Local Colour Table ----
            for (int i = 0; i < tableSize; i++)
            {
                if (i < paletteCount)
                {
                    output.WriteByte(paletteR[i]);
                    output.WriteByte(paletteG[i]);
                    output.WriteByte(paletteB[i]);
                }
                else
                {
                    output.WriteByte(0); output.WriteByte(0); output.WriteByte(0);
                }
            }

            // ---- LZW-compressed image data ----
            int minCodeSize = Math.Max(2, tableBits);
            output.WriteByte((byte)minCodeSize);
            var compressed = GifLzwEncoder.Encode(indices, minCodeSize);
            WriteSubBlocks(output, compressed);
            output.WriteByte(0); // block terminator
        }

        // ---- Trailer ----
        output.WriteByte(0x3B);

        var bytes = output.ToArray();
        await writer.AsStream().WriteAsync(bytes, cancellationToken);
        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    /// <summary>
    /// GIF spec packs LZW output into "data sub-blocks" — each preceded
    /// by a length byte, max 255 data bytes each, terminated by a
    /// zero-length block (written by the caller).
    /// </summary>
    private static void WriteSubBlocks(Stream s, byte[] data)
    {
        int p = 0;
        while (p < data.Length)
        {
            int n = Math.Min(255, data.Length - p);
            s.WriteByte((byte)n);
            s.Write(data, p, n);
            p += n;
        }
    }
}

/// <summary>
/// LZW encoder for the GIF data section. Variable-bit-width codes
/// starting at <c>minCodeSize + 1</c> bits, growing to 12 bits as the
/// dictionary fills. Emits a CLEAR code at start, an END code at
/// finish, and a CLEAR whenever the dictionary saturates at 4096
/// entries (so the decoder rebuilds in lockstep).
/// </summary>
internal static class GifLzwEncoder
{
    /// <summary>
    /// Encode <paramref name="indices"/> as raw LZW bytes (no
    /// sub-block framing — the saver wraps the result in 255-byte
    /// sub-blocks).
    /// </summary>
    public static byte[] Encode(byte[] indices, int minCodeSize)
    {
        if (minCodeSize < 2 || minCodeSize > 8) throw new ArgumentOutOfRangeException(nameof(minCodeSize));

        int clearCode = 1 << minCodeSize;
        int endCode = clearCode + 1;
        const int maxCode = 4096;

        // Dictionary entry: (prefix code, suffix byte) → code.
        // Key packs prefix in the high 32 bits, suffix in the low 8.
        var dict = new Dictionary<long, int>(4096);
        int nextCode = endCode + 1;
        int codeSize = minCodeSize + 1;

        var bits = new BitWriter();
        bits.Write((uint)clearCode, codeSize);

        if (indices.Length == 0)
        {
            bits.Write((uint)endCode, codeSize);
            return bits.ToArray();
        }

        int currentCode = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            byte k = indices[i];
            long key = ((long)currentCode << 8) | k;
            if (dict.TryGetValue(key, out int found))
            {
                currentCode = found;
                continue;
            }

            // Output the current code, then add (currentCode + k) to dict.
            bits.Write((uint)currentCode, codeSize);
            if (nextCode < maxCode)
            {
                dict[key] = nextCode++;
                if (nextCode > (1 << codeSize) && codeSize < 12)
                    codeSize++;
            }
            else
            {
                // Dictionary full: emit CLEAR and start fresh.
                bits.Write((uint)clearCode, codeSize);
                dict.Clear();
                nextCode = endCode + 1;
                codeSize = minCodeSize + 1;
            }
            currentCode = k;
        }

        bits.Write((uint)currentCode, codeSize);
        bits.Write((uint)endCode, codeSize);
        return bits.ToArray();
    }

    /// <summary>
    /// LSB-first variable-width bit writer. GIF packs codes
    /// least-significant-bit-first within each byte, then
    /// least-significant-byte-first across the stream.
    /// </summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private uint _accum;
        private int _bitsInAccum;

        public void Write(uint value, int bitCount)
        {
            _accum |= value << _bitsInAccum;
            _bitsInAccum += bitCount;
            while (_bitsInAccum >= 8)
            {
                _bytes.Add((byte)(_accum & 0xFF));
                _accum >>= 8;
                _bitsInAccum -= 8;
            }
        }

        public byte[] ToArray()
        {
            if (_bitsInAccum > 0)
                _bytes.Add((byte)(_accum & 0xFF));
            return _bytes.ToArray();
        }
    }
}
