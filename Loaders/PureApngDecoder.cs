using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace CosmoImage.Loaders;

/// <summary>
/// Pure-managed Animated-PNG (APNG) decoder. Parses the standard
/// PNG chunk stream plus the APNG-specific <c>acTL</c> (animation
/// control), <c>fcTL</c> (frame control), and <c>fdAT</c> (frame
/// data) chunks; reuses the static-PNG zlib + filter-unfilter
/// pipeline from <see cref="PurePngDecoder"/> for each frame's
/// pixel decode.
///
/// <para>Output is RGBA throughout — the dispose / blend operations
/// require alpha even when the source colour type lacks it.</para>
///
/// <para>Supported configurations: 8-bit non-interlaced; colour
/// types 6 (RGBA) and 2 (RGB, expanded to RGBA); fcTL precedes IDAT
/// (the common APNG layout where IDAT is frame 0). Returns
/// <c>null</c> for greyscale / palette APNG, IDAT-as-fallback layout,
/// 16-bit depth, or interlaced — caller falls back to static-PNG
/// decode of the IDAT alone (the spec mandates this fallback works).</para>
/// </summary>
internal static class PureApngDecoder
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Decoded frame data — produced by <see cref="TryDecode"/>.</summary>
    public sealed class Result
    {
        public required int CanvasWidth { get; init; }
        public required int CanvasHeight { get; init; }
        public required int FrameCount { get; init; }
        /// <summary>Per-frame delay in centiseconds (1/100 sec) — matches the GIF/APNG saver convention.</summary>
        public required IReadOnlyList<int> DelaysCentiseconds { get; init; }
        /// <summary>Stacked-frames RGBA pixel buffer; height = CanvasHeight · FrameCount, stride = CanvasWidth · 4.</summary>
        public required byte[] Pixels { get; init; }
    }

    /// <summary>
    /// Parse and decode an APNG byte stream. Returns <c>null</c> if
    /// the stream isn't APNG (no <c>acTL</c>) or uses an unsupported
    /// configuration.
    /// </summary>
    public static Result? TryDecode(byte[] pngBytes)
    {
        if (pngBytes == null || pngBytes.Length < 8) return null;
        for (int i = 0; i < 8; i++) if (pngBytes[i] != Signature[i]) return null;

        // Pass 1: walk chunks, capturing IHDR, acTL, frame definitions.
        int width = 0, height = 0;
        byte bitDepth = 0, colorType = 0, interlace = 0;
        int numFrames = 0;
        bool sawActl = false;
        bool fcTlBeforeIdat = false;

        var frames = new List<FrameInfo>();
        FrameInfo? current = null;
        bool sawIdat = false;

        int p = 8;
        while (p + 8 <= pngBytes.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(p, 4));
            uint type = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(p + 4, 4));
            int dataStart = p + 8;
            if (dataStart + (int)length + 4 > pngBytes.Length) return null;

            switch (type)
            {
                case 0x49484452:  // IHDR
                    if (length != 13) return null;
                    width = BinaryPrimitives.ReadInt32BigEndian(pngBytes.AsSpan(dataStart, 4));
                    height = BinaryPrimitives.ReadInt32BigEndian(pngBytes.AsSpan(dataStart + 4, 4));
                    bitDepth = pngBytes[dataStart + 8];
                    colorType = pngBytes[dataStart + 9];
                    interlace = pngBytes[dataStart + 12];
                    if (bitDepth != 8 || interlace != 0) return null;
                    if (colorType != 2 && colorType != 6) return null;  // only RGB / RGBA for now
                    break;

                case 0x6163544C:  // acTL
                    if (length < 8) return null;
                    numFrames = (int)BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(dataStart, 4));
                    sawActl = true;
                    break;

                case 0x6663544C:  // fcTL
                    if (length < 26) return null;
                    if (!sawIdat) fcTlBeforeIdat = true;
                    current = new FrameInfo
                    {
                        Width = (int)BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(dataStart + 4, 4)),
                        Height = (int)BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(dataStart + 8, 4)),
                        XOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(dataStart + 12, 4)),
                        YOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(dataStart + 16, 4)),
                        DelayNum = BinaryPrimitives.ReadUInt16BigEndian(pngBytes.AsSpan(dataStart + 20, 2)),
                        DelayDen = BinaryPrimitives.ReadUInt16BigEndian(pngBytes.AsSpan(dataStart + 22, 2)),
                        DisposeOp = pngBytes[dataStart + 24],
                        BlendOp = pngBytes[dataStart + 25],
                        Compressed = new MemoryStream(),
                    };
                    frames.Add(current);
                    break;

                case 0x49444154:  // IDAT
                    sawIdat = true;
                    if (current != null && fcTlBeforeIdat)
                        current.Compressed.Write(pngBytes, dataStart, (int)length);
                    break;

                case 0x66644154:  // fdAT
                    if (current != null && length >= 4)
                    {
                        // Skip the 4-byte sequence_number prefix.
                        current.Compressed.Write(pngBytes, dataStart + 4, (int)length - 4);
                    }
                    break;

                case 0x49454E44:  // IEND
                    p = int.MaxValue / 2;
                    break;
            }
            p = dataStart + (int)length + 4;
        }

        if (!sawActl || numFrames == 0 || frames.Count == 0) return null;
        if (!fcTlBeforeIdat) return null;  // IDAT-as-fallback layout not supported here
        if (frames.Count != numFrames) return null;

        // Pass 2: decode each frame's pixel data + composite onto canvas.
        int srcBpp = colorType == 2 ? 3 : 4;
        bool needsAlphaExpand = colorType == 2;

        var canvas = new byte[width * height * 4];  // start fully transparent
        byte[]? prevSnapshot = null;

        var output = new byte[width * height * 4 * numFrames];
        var delays = new int[numFrames];

        for (int f = 0; f < numFrames; f++)
        {
            var frame = frames[f];
            if (frame.Width <= 0 || frame.Height <= 0
                || frame.XOffset < 0 || frame.YOffset < 0
                || frame.XOffset + frame.Width > width
                || frame.YOffset + frame.Height > height)
                return null;

            // Decompress frame's combined IDAT/fdAT data.
            byte[] raw;
            try
            {
                frame.Compressed.Position = 0;
                using var inflate = new ZLibStream(frame.Compressed, CompressionMode.Decompress);
                using var ms = new MemoryStream();
                inflate.CopyTo(ms);
                raw = ms.ToArray();
            }
            catch { return null; }

            // Unfilter — frame-local stride.
            int stride = frame.Width * srcBpp;
            int expected = (stride + 1) * frame.Height;
            if (raw.Length < expected) return null;
            var frameBytes = new byte[stride * frame.Height];
            if (!UnfilterRows(raw, frameBytes, frame.Width, frame.Height, srcBpp, stride))
                return null;

            // Snapshot canvas BEFORE rendering this frame, if the dispose
            // op for THIS frame is PREVIOUS (we'll need to restore later).
            if (frame.DisposeOp == DisposePrevious)
                prevSnapshot = (byte[])canvas.Clone();

            // Composite frame onto canvas.
            CompositeFrame(canvas, width, frameBytes, frame, srcBpp, needsAlphaExpand);

            // Emit canvas state as this frame's output.
            Buffer.BlockCopy(canvas, 0, output, f * width * height * 4, width * height * 4);

            // Compute delay (centiseconds = 1/100 sec).
            int den = frame.DelayDen == 0 ? 100 : frame.DelayDen;
            delays[f] = (int)(frame.DelayNum * 100 / den);

            // Apply post-frame dispose for the NEXT frame's canvas state.
            switch (frame.DisposeOp)
            {
                case DisposeBackground:
                    ClearRegion(canvas, width, frame.XOffset, frame.YOffset, frame.Width, frame.Height);
                    break;
                case DisposePrevious:
                    if (prevSnapshot != null) Buffer.BlockCopy(prevSnapshot, 0, canvas, 0, canvas.Length);
                    break;
                // case DisposeNone: leave canvas as-is.
            }
        }

        return new Result
        {
            CanvasWidth = width,
            CanvasHeight = height,
            FrameCount = numFrames,
            DelaysCentiseconds = delays,
            Pixels = output,
        };
    }

    private const byte DisposeNone = 0;
    private const byte DisposeBackground = 1;
    private const byte DisposePrevious = 2;
    private const byte BlendSource = 0;
    private const byte BlendOver = 1;

    private sealed class FrameInfo
    {
        public int Width, Height, XOffset, YOffset;
        public ushort DelayNum, DelayDen;
        public byte DisposeOp, BlendOp;
        public required MemoryStream Compressed { get; init; }
    }

    /// <summary>
    /// Apply the frame's pixel data onto the RGBA canvas at
    /// (x_offset, y_offset). When source has no alpha (colour type 2),
    /// each pixel is treated as fully opaque. <c>blend_op = SOURCE</c>
    /// overwrites; <c>blend_op = OVER</c> alpha-composites.
    /// </summary>
    private static void CompositeFrame(byte[] canvas, int canvasWidth,
        byte[] frame, FrameInfo info, int srcBpp, bool needsAlphaExpand)
    {
        for (int y = 0; y < info.Height; y++)
        {
            int srcRow = y * info.Width * srcBpp;
            int dstRow = ((info.YOffset + y) * canvasWidth + info.XOffset) * 4;
            for (int x = 0; x < info.Width; x++)
            {
                byte sR, sG, sB, sA;
                if (needsAlphaExpand)
                {
                    sR = frame[srcRow + x * srcBpp + 0];
                    sG = frame[srcRow + x * srcBpp + 1];
                    sB = frame[srcRow + x * srcBpp + 2];
                    sA = 255;
                }
                else
                {
                    sR = frame[srcRow + x * srcBpp + 0];
                    sG = frame[srcRow + x * srcBpp + 1];
                    sB = frame[srcRow + x * srcBpp + 2];
                    sA = frame[srcRow + x * srcBpp + 3];
                }

                int dstOff = dstRow + x * 4;
                if (info.BlendOp == BlendSource || sA == 255)
                {
                    canvas[dstOff + 0] = sR;
                    canvas[dstOff + 1] = sG;
                    canvas[dstOff + 2] = sB;
                    canvas[dstOff + 3] = sA;
                }
                else if (sA == 0)
                {
                    // Fully transparent source — leave canvas untouched.
                }
                else
                {
                    // Standard "src OVER dst" alpha compositing.
                    byte dR = canvas[dstOff + 0], dG = canvas[dstOff + 1];
                    byte dB = canvas[dstOff + 2], dA = canvas[dstOff + 3];
                    int srcA = sA;
                    int invA = 255 - srcA;
                    int outA = srcA + (dA * invA + 127) / 255;
                    if (outA == 0)
                    {
                        canvas[dstOff + 0] = canvas[dstOff + 1] = canvas[dstOff + 2] = canvas[dstOff + 3] = 0;
                        continue;
                    }
                    // Premultiplied combination, then un-premultiply.
                    int outR = (sR * srcA + dR * dA * invA / 255 + outA / 2) / outA;
                    int outG = (sG * srcA + dG * dA * invA / 255 + outA / 2) / outA;
                    int outB = (sB * srcA + dB * dA * invA / 255 + outA / 2) / outA;
                    canvas[dstOff + 0] = (byte)Math.Clamp(outR, 0, 255);
                    canvas[dstOff + 1] = (byte)Math.Clamp(outG, 0, 255);
                    canvas[dstOff + 2] = (byte)Math.Clamp(outB, 0, 255);
                    canvas[dstOff + 3] = (byte)outA;
                }
            }
        }
    }

    private static void ClearRegion(byte[] canvas, int canvasWidth, int x, int y, int w, int h)
    {
        for (int yy = 0; yy < h; yy++)
        {
            int rowOff = ((y + yy) * canvasWidth + x) * 4;
            Array.Clear(canvas, rowOff, w * 4);
        }
    }

    /// <summary>
    /// Reverse PNG scanline filters into a flat unfiltered buffer —
    /// same algorithm as <see cref="PurePngDecoder"/> but operating on
    /// frame-local dimensions rather than canvas dimensions.
    /// </summary>
    private static bool UnfilterRows(byte[] raw, byte[] dst, int width, int height, int bpp, int stride)
    {
        int rawPos = 0;
        for (int y = 0; y < height; y++)
        {
            if (rawPos >= raw.Length) return false;
            byte filter = raw[rawPos++];
            int rowOff = y * stride;
            int aboveOff = (y - 1) * stride;
            if (rawPos + stride > raw.Length) return false;
            switch (filter)
            {
                case 0:
                    Buffer.BlockCopy(raw, rawPos, dst, rowOff, stride);
                    break;
                case 1:
                    for (int x = 0; x < stride; x++)
                    {
                        byte left = x < bpp ? (byte)0 : dst[rowOff + x - bpp];
                        dst[rowOff + x] = (byte)(raw[rawPos + x] + left);
                    }
                    break;
                case 2:
                    for (int x = 0; x < stride; x++)
                    {
                        byte above = y == 0 ? (byte)0 : dst[aboveOff + x];
                        dst[rowOff + x] = (byte)(raw[rawPos + x] + above);
                    }
                    break;
                case 3:
                    for (int x = 0; x < stride; x++)
                    {
                        byte left = x < bpp ? (byte)0 : dst[rowOff + x - bpp];
                        byte above = y == 0 ? (byte)0 : dst[aboveOff + x];
                        dst[rowOff + x] = (byte)(raw[rawPos + x] + ((left + above) / 2));
                    }
                    break;
                case 4:
                    for (int x = 0; x < stride; x++)
                    {
                        byte left = x < bpp ? (byte)0 : dst[rowOff + x - bpp];
                        byte above = y == 0 ? (byte)0 : dst[aboveOff + x];
                        byte upLeft = (y == 0 || x < bpp) ? (byte)0 : dst[aboveOff + x - bpp];
                        dst[rowOff + x] = (byte)(raw[rawPos + x] + Paeth(left, above, upLeft));
                    }
                    break;
                default:
                    return false;
            }
            rawPos += stride;
        }
        return true;
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }
}
