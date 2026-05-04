using System;
using System.Collections.Generic;
using System.IO;

namespace CosmoImage.Loaders;

/// <summary>
/// Pure-managed GIF87a / GIF89a decoder. Handles single-frame and
/// animated streams with full LZW decompression, Graphics Control
/// Extension (delay / transparency / disposal), interlaced frames,
/// global + local colour tables, and frame composition onto the
/// logical screen canvas.
///
/// <para>Output is stacked-frames RGBA matching the existing
/// animated-format convention used by <see cref="VipsGifLoader"/>:
/// canvas height × frame count, with <c>n-pages</c>, <c>page-height</c>,
/// and <c>animation-delays</c> set on the loaded image.</para>
///
/// <para>Returns <c>null</c> for malformed streams; caller falls
/// back to Magick.NET on failure.</para>
/// </summary>
internal static class PureGifDecoder
{
    public sealed class Result
    {
        public required int CanvasWidth { get; init; }
        public required int CanvasHeight { get; init; }
        public required int FrameCount { get; init; }
        /// <summary>Per-frame delay in centiseconds (1/100 sec).</summary>
        public required IReadOnlyList<int> DelaysCentiseconds { get; init; }
        /// <summary>Stacked-frames RGBA buffer (CanvasW · CanvasH · FrameCount · 4 bytes).</summary>
        public required byte[] Pixels { get; init; }
        public string? Comment { get; init; }
    }

    public static Result? TryDecode(byte[] gifBytes)
    {
        if (gifBytes == null || gifBytes.Length < 13) return null;
        // Header: "GIF87a" or "GIF89a".
        if (gifBytes[0] != 'G' || gifBytes[1] != 'I' || gifBytes[2] != 'F') return null;

        try
        {
            return Parse(gifBytes);
        }
        catch
        {
            return null;
        }
    }

    private static Result Parse(byte[] data)
    {
        int p = 6;  // skip "GIFxxa"
        int canvasW = ReadU16Le(data, p); p += 2;
        int canvasH = ReadU16Le(data, p); p += 2;
        byte packed = data[p++];
        byte bgIndex = data[p++];
        p++;  // pixel aspect ratio

        bool hasGct = (packed & 0x80) != 0;
        int gctSize = hasGct ? 1 << ((packed & 0x07) + 1) : 0;
        byte[]? gct = null;
        if (hasGct)
        {
            gct = new byte[gctSize * 3];
            Buffer.BlockCopy(data, p, gct, 0, gct.Length);
            p += gct.Length;
        }

        // Canvas state: starts as fully transparent.
        var canvas = new byte[canvasW * canvasH * 4];
        byte[]? prevSnapshot = null;

        var frameBufs = new List<byte[]>();
        var delays = new List<int>();
        string? comment = null;

        // Per-frame state from GCE — applies to the NEXT image block.
        int pendingDelay = 0;
        int transparentIndex = -1;
        byte pendingDisposal = 0;
        byte? gceTransparencyFlag = null;

        while (p < data.Length)
        {
            byte block = data[p++];
            if (block == 0x3B)  // trailer
            {
                break;
            }
            if (block == 0x21)  // extension introducer
            {
                if (p >= data.Length) break;
                byte label = data[p++];
                switch (label)
                {
                    case 0xF9:  // Graphics Control Extension
                        if (p + 6 > data.Length) throw new InvalidDataException("truncated GCE");
                        // Block size byte (always 0x04), packed, delay (LE), trans index, terminator.
                        if (data[p] != 0x04) throw new InvalidDataException("bad GCE size");
                        byte gcePacked = data[p + 1];
                        pendingDisposal = (byte)((gcePacked >> 2) & 0x07);
                        gceTransparencyFlag = (byte)(gcePacked & 0x01);
                        pendingDelay = ReadU16Le(data, p + 2);
                        transparentIndex = (gcePacked & 0x01) != 0 ? data[p + 4] : -1;
                        // Terminator at p+5 should be 0.
                        p += 6;
                        break;
                    case 0xFE:  // Comment Extension
                        var commentBytes = ReadSubBlocks(data, ref p);
                        if (commentBytes != null && comment == null)
                            comment = System.Text.Encoding.ASCII.GetString(commentBytes);
                        break;
                    default:
                        // Plain Text (0x01), Application (0xFF), or unknown — skip
                        // any fixed-size header bytes, then drain sub-blocks.
                        if (label == 0x01)
                        {
                            // Plain Text Extension has 13-byte header + sub-blocks.
                            if (p + 13 > data.Length) throw new InvalidDataException("truncated PTE");
                            if (data[p] != 0x0C) throw new InvalidDataException("bad PTE size");
                            p += 13;
                            ReadSubBlocks(data, ref p);
                        }
                        else
                        {
                            // Application or unknown extension — skip variable-size
                            // sub-blocks. Application ext has 11-byte fixed header.
                            if (label == 0xFF && p < data.Length && data[p] == 0x0B)
                                p += 12;
                            ReadSubBlocks(data, ref p);
                        }
                        break;
                }
                continue;
            }
            if (block == 0x2C)  // Image Descriptor
            {
                if (p + 9 > data.Length) throw new InvalidDataException("truncated image descriptor");
                int frameLeft = ReadU16Le(data, p);
                int frameTop = ReadU16Le(data, p + 2);
                int frameW = ReadU16Le(data, p + 4);
                int frameH = ReadU16Le(data, p + 6);
                byte imgPacked = data[p + 8];
                p += 9;
                bool hasLct = (imgPacked & 0x80) != 0;
                bool interlaced = (imgPacked & 0x40) != 0;
                int lctSize = hasLct ? 1 << ((imgPacked & 0x07) + 1) : 0;
                byte[]? lct = null;
                if (hasLct)
                {
                    lct = new byte[lctSize * 3];
                    Buffer.BlockCopy(data, p, lct, 0, lct.Length);
                    p += lct.Length;
                }
                var palette = lct ?? gct;
                if (palette == null) throw new InvalidDataException("no palette");

                // LZW-compressed indexed pixel data follows.
                byte lzwMinCodeSize = data[p++];
                var compressed = ReadSubBlocks(data, ref p) ?? Array.Empty<byte>();
                var indices = LzwDecode(compressed, lzwMinCodeSize, frameW * frameH);

                // De-interlace if needed.
                if (interlaced) indices = Deinterlace(indices, frameW, frameH);

                // Snapshot canvas BEFORE rendering if this frame's disposal
                // is PREVIOUS — we'll restore from this snapshot for the
                // NEXT frame.
                if (pendingDisposal == 3)  // PREVIOUS
                    prevSnapshot = (byte[])canvas.Clone();

                // Composite indices onto the canvas at (frameLeft, frameTop).
                for (int y = 0; y < frameH; y++)
                {
                    int dstY = frameTop + y;
                    if (dstY < 0 || dstY >= canvasH) continue;
                    for (int x = 0; x < frameW; x++)
                    {
                        int dstX = frameLeft + x;
                        if (dstX < 0 || dstX >= canvasW) continue;
                        int srcIdx = y * frameW + x;
                        if (srcIdx >= indices.Length) break;
                        byte palIdx = indices[srcIdx];
                        if (palIdx == transparentIndex) continue;  // leave canvas pixel
                        int paletteOff = palIdx * 3;
                        if (paletteOff + 3 > palette.Length) continue;
                        int dstOff = (dstY * canvasW + dstX) * 4;
                        canvas[dstOff + 0] = palette[paletteOff + 0];
                        canvas[dstOff + 1] = palette[paletteOff + 1];
                        canvas[dstOff + 2] = palette[paletteOff + 2];
                        canvas[dstOff + 3] = 255;
                    }
                }

                // Snapshot canvas AS THIS FRAME'S OUTPUT.
                frameBufs.Add((byte[])canvas.Clone());
                delays.Add(pendingDelay);

                // Apply disposal for the NEXT frame:
                //   0 = unspecified → no action (treat as 1)
                //   1 = do not dispose (keep canvas as-is)
                //   2 = restore to background colour (fill frame region with
                //       transparent — even though spec says "background",
                //       most decoders use transparent here for animations)
                //   3 = restore to previous (use snapshot)
                switch (pendingDisposal)
                {
                    case 2:
                        ClearRegion(canvas, canvasW, frameLeft, frameTop, frameW, frameH);
                        break;
                    case 3:
                        if (prevSnapshot != null)
                            Buffer.BlockCopy(prevSnapshot, 0, canvas, 0, canvas.Length);
                        break;
                }

                // Reset GCE state for the next frame (each frame's GCE
                // applies only to that frame).
                pendingDelay = 0;
                pendingDisposal = 0;
                transparentIndex = -1;
                continue;
            }
            // Unknown / malformed introducer — abort.
            throw new InvalidDataException($"unknown block 0x{block:X2} at offset {p - 1}");
        }

        if (frameBufs.Count == 0) throw new InvalidDataException("no frames");

        // Concatenate frame buffers into a single stacked output.
        int frameSize = canvasW * canvasH * 4;
        var output = new byte[frameSize * frameBufs.Count];
        for (int i = 0; i < frameBufs.Count; i++)
            Buffer.BlockCopy(frameBufs[i], 0, output, i * frameSize, frameSize);

        return new Result
        {
            CanvasWidth = canvasW,
            CanvasHeight = canvasH,
            FrameCount = frameBufs.Count,
            DelaysCentiseconds = delays,
            Pixels = output,
            Comment = comment,
        };
    }

    /// <summary>
    /// LZW decompressor specialised for GIF semantics (variable code
    /// width starting at <c>minCodeSize+1</c>; clear + EOI codes; growing
    /// dictionary up to 12 bits). Outputs the indexed pixel byte stream
    /// (one palette index per pixel).
    /// </summary>
    private static byte[] LzwDecode(byte[] compressed, byte minCodeSize, int expectedSize)
    {
        if (minCodeSize < 2 || minCodeSize > 8) throw new InvalidDataException("bad LZW min code size");
        int clearCode = 1 << minCodeSize;
        int eoiCode = clearCode + 1;
        var output = new byte[expectedSize];
        int outPos = 0;

        // Dictionary entries — each is a list of bytes. Cap at 4096.
        var dict = new List<byte[]>(4096);
        int currCodeSize;
        int nextCode;
        ResetDict();

        void ResetDict()
        {
            dict.Clear();
            for (int i = 0; i < clearCode; i++) dict.Add(new[] { (byte)i });
            dict.Add(Array.Empty<byte>());  // clear placeholder
            dict.Add(Array.Empty<byte>());  // EOI placeholder
            currCodeSize = minCodeSize + 1;
            nextCode = clearCode + 2;
        }

        // Bit-stream reader.
        int bitBuf = 0;
        int bitCount = 0;
        int bytePos = 0;

        int ReadCode()
        {
            while (bitCount < currCodeSize)
            {
                if (bytePos >= compressed.Length) return -1;
                bitBuf |= compressed[bytePos++] << bitCount;
                bitCount += 8;
            }
            int code = bitBuf & ((1 << currCodeSize) - 1);
            bitBuf >>= currCodeSize;
            bitCount -= currCodeSize;
            return code;
        }

        int prevCode = -1;
        while (true)
        {
            int code = ReadCode();
            if (code < 0) break;
            if (code == eoiCode) break;
            if (code == clearCode)
            {
                ResetDict();
                prevCode = -1;
                continue;
            }
            byte[] entry;
            if (code < dict.Count)
            {
                entry = dict[code];
            }
            else if (code == dict.Count && prevCode >= 0)
            {
                // KwKwK case.
                var prev = dict[prevCode];
                entry = new byte[prev.Length + 1];
                Buffer.BlockCopy(prev, 0, entry, 0, prev.Length);
                entry[prev.Length] = prev[0];
            }
            else
            {
                throw new InvalidDataException("bad LZW code");
            }

            // Emit.
            int copyLen = Math.Min(entry.Length, output.Length - outPos);
            if (copyLen > 0)
            {
                Buffer.BlockCopy(entry, 0, output, outPos, copyLen);
                outPos += copyLen;
            }

            // Add new dictionary entry: prev + first byte of entry.
            if (prevCode >= 0 && nextCode < 4096)
            {
                var prev = dict[prevCode];
                var newEntry = new byte[prev.Length + 1];
                Buffer.BlockCopy(prev, 0, newEntry, 0, prev.Length);
                newEntry[prev.Length] = entry[0];
                dict.Add(newEntry);
                nextCode++;
                // Grow code width when dict reaches 2^currCodeSize, capped at 12.
                if (nextCode == (1 << currCodeSize) && currCodeSize < 12)
                    currCodeSize++;
            }
            prevCode = code;
        }
        return output;
    }

    /// <summary>
    /// De-interlace a GIF interlaced frame. Interlaced order is 4 passes:
    ///   pass 1: rows 0, 8, 16, ... (every 8th from row 0)
    ///   pass 2: rows 4, 12, 20, ... (every 8th from row 4)
    ///   pass 3: rows 2, 6, 10, ... (every 4th from row 2)
    ///   pass 4: rows 1, 3, 5, ...  (every 2nd from row 1)
    /// </summary>
    private static byte[] Deinterlace(byte[] src, int width, int height)
    {
        var dst = new byte[src.Length];
        int srcRow = 0;
        for (int pass = 0; pass < 4; pass++)
        {
            int start = pass switch { 0 => 0, 1 => 4, 2 => 2, _ => 1 };
            int step = pass switch { 0 => 8, 1 => 8, 2 => 4, _ => 2 };
            for (int y = start; y < height; y += step)
            {
                if (srcRow * width + width > src.Length) break;
                Buffer.BlockCopy(src, srcRow * width, dst, y * width, width);
                srcRow++;
            }
        }
        return dst;
    }

    /// <summary>
    /// Read GIF "data sub-blocks" — a sequence of (length-byte, data)
    /// pairs terminated by a 0-length block. Returns the concatenated
    /// data, or null if malformed.
    /// </summary>
    private static byte[]? ReadSubBlocks(byte[] data, ref int p)
    {
        var ms = new MemoryStream();
        while (p < data.Length)
        {
            byte len = data[p++];
            if (len == 0) return ms.ToArray();
            if (p + len > data.Length) return null;
            ms.Write(data, p, len);
            p += len;
        }
        return null;
    }

    private static int ReadU16Le(byte[] data, int off) => data[off] | (data[off + 1] << 8);

    private static void ClearRegion(byte[] canvas, int canvasW, int x, int y, int w, int h)
    {
        for (int yy = 0; yy < h; yy++)
        {
            int rowOff = ((y + yy) * canvasW + x) * 4;
            int clear = Math.Min(w * 4, canvas.Length - rowOff);
            if (rowOff < 0 || clear <= 0) continue;
            Array.Clear(canvas, rowOff, clear);
        }
    }
}
