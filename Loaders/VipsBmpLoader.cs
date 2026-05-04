using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CosmoImage.Loaders;

public static class VipsBmpLoader
{
    public static async ValueTask<bool> IsBmpAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(2, cancellationToken);
        if (sniff.Length < 2) return false;

        var span = sniff.Span;
        return span[0] == (byte)'B' && span[1] == (byte)'M';
    }

    public static async ValueTask<VipsImage?> LoadHeaderAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsBmpAsync(source, cancellationToken))
            return null;

        // BMP File Header is 14 bytes
        var fileHeader = new byte[14];
        int read = await source.ReadAsync(fileHeader, cancellationToken);
        if (read < 14) return null;

        // DIB Header size (4 bytes)
        var dibSizeBuffer = new byte[4];
        read = await source.ReadAsync(dibSizeBuffer, cancellationToken);
        if (read < 4) return null;

        uint dibSize = BinaryPrimitives.ReadUInt32LittleEndian(dibSizeBuffer);
        if (dibSize < 12) return null; // Too small for width/height

        var dibData = new byte[dibSize - 4];
        read = await source.ReadAsync(dibData, cancellationToken);
        if (read < dibData.Length) return null;

        int width, height;
        ushort bpp;

        if (dibSize == 12) // BITMAPCOREHEADER
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(dibData.AsSpan(0, 2));
            height = BinaryPrimitives.ReadUInt16LittleEndian(dibData.AsSpan(2, 2));
            bpp = BinaryPrimitives.ReadUInt16LittleEndian(dibData.AsSpan(6, 2));
        }
        else // BITMAPINFOHEADER and newer
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(dibData.AsSpan(0, 4));
            height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(dibData.AsSpan(4, 4)));
            bpp = BinaryPrimitives.ReadUInt16LittleEndian(dibData.AsSpan(10, 2));
        }

        int bands = bpp switch
        {
            1 or 4 or 8 => 3, // Paletted, expanded to RGB
            16 or 24 => 3,    // RGB
            32 => 4,          // RGBA
            _ => 3
        };

        return new VipsImage
        {
            Width = width,
            Height = height,
            Bands = bands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = bands == 4 ? VipsInterpretation.RGB : VipsInterpretation.RGB,
            XRes = 1.0,
            YRes = 1.0
        };
    }

    /// <summary>
    /// Full BMP load with pixel data. Tries the pure-C# fast path first
    /// (24bpp BGR / 32bpp BGRA, BI_RGB compression, BITMAPINFOHEADER) which
    /// covers the modern common cases without a Magick.NET hop. Falls back
    /// to Magick for paletted (1/4/8 bpp), 16-bit RGB555, RLE-compressed
    /// (BI_RLE4 / BI_RLE8), BITFIELDS-masked, V4/V5 colour-space variants,
    /// and any header version we don't recognise.
    /// </summary>
    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsBmpAsync(source, cancellationToken))
            return null;

        var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
        }

        var imageBytes = ms.ToArray();

        // Fast path: 24/32 bpp BI_RGB.
        var fast = TryDecodePureCSharp(imageBytes);
        if (fast != null) return fast;

        // Fallback to Magick for everything else.
        return DecodeViaMagick(imageBytes);
    }

    /// <summary>
    /// Pure-C# decoder for the common BMP variants (BITMAPINFOHEADER,
    /// 24bpp BGR or 32bpp BGRA, BI_RGB compression). Returns
    /// <see langword="null"/> when the file uses a variant we don't handle,
    /// signalling the caller to fall back to Magick.
    /// </summary>
    private static VipsImage? TryDecodePureCSharp(byte[] bytes)
    {
        if (bytes.Length < 14 + 40) return null;
        if (bytes[0] != 'B' || bytes[1] != 'M') return null;

        uint pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(10, 4));
        uint dibSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(14, 4));
        // Only BITMAPINFOHEADER (40) or its V4/V5 prefixes for the fast path.
        if (dibSize < 40) return null;

        int width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18, 4));
        int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22, 4));
        ushort planes = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26, 2));
        ushort bpp = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2));
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(30, 4));
        uint colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(46, 4));

        if (planes != 1) return null;
        if (width <= 0) return null;
        // Reject only configurations we don't handle. Supported:
        //   BI_RGB (0) at 1/4/8/16/24/32 bpp
        //   BI_RLE8 (1) at 8 bpp
        // Supported compressions:
        //   BI_RGB (0): uncompressed at 1/4/8/16/24/32 bpp
        //   BI_RLE8 (1): RLE at 8 bpp
        //   BI_RLE4 (2): RLE at 4 bpp
        //   BI_BITFIELDS (3): 16 / 32 bpp with caller-supplied channel masks
        if (compression != 0 && compression != 1 && compression != 2 && compression != 3)
            return null;
        if (compression == 1 && bpp != 8) return null;
        if (compression == 2 && bpp != 4) return null;
        if (compression == 3 && bpp != 16 && bpp != 32) return null;
        if (bpp != 1 && bpp != 4 && bpp != 8 && bpp != 16 && bpp != 24 && bpp != 32) return null;

        int height = Math.Abs(rawHeight);
        if (height == 0) return null;
        bool bottomUp = rawHeight > 0;

        // Palette for ≤ 8 bpp. Each entry is 4 bytes BGRA (A reserved/0).
        byte[]? palette = null;
        if (bpp <= 8)
        {
            int paletteEntries = colorsUsed > 0 ? (int)colorsUsed : (1 << bpp);
            int paletteOff = 14 + (int)dibSize;
            if (paletteOff + paletteEntries * 4 > bytes.Length) return null;
            palette = new byte[paletteEntries * 4];
            Buffer.BlockCopy(bytes, paletteOff, palette, 0, palette.Length);
        }

        // 16 / 24 / 32 bpp produce 3 (RGB) or 4 (RGBA) bands.
        // ≤ 8 bpp paletted is expanded to 3-band RGB.
        int outBands = (bpp == 32) ? 4 : 3;
        var pixels = new byte[width * height * outBands];

        // BI_BITFIELDS: channel masks live just after the DIB header
        // (12 bytes for V3, 16 bytes for V4/V5 with alpha mask).
        uint maskR = 0, maskG = 0, maskB = 0, maskA = 0;
        if (compression == 3)
        {
            int maskOff = 14 + (int)dibSize;
            // V3 (40-byte DIB) puts masks BEFORE the palette/pixel data;
            // V4/V5 includes them in the header itself. Either way they
            // sit at offset 14+40 for V3 or 14+108 for V4 etc.
            if (dibSize == 40) maskOff = 14 + 40;
            else maskOff = 14 + 40;  // masks always at the V3 spot
            if (maskOff + 12 > bytes.Length) return null;
            maskR = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(maskOff, 4));
            maskG = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(maskOff + 4, 4));
            maskB = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(maskOff + 8, 4));
            if (bpp == 32 && maskOff + 16 <= bytes.Length)
                maskA = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(maskOff + 12, 4));
            if (maskA != 0) outBands = 4;
            // Re-allocate if alpha lifted us to 4-band.
            pixels = new byte[width * height * outBands];
        }

        bool ok = compression switch
        {
            0 => DecodeBiRgb(bytes, pixelOffset, pixels, width, height,
                bpp, bottomUp, outBands, palette),
            1 => DecodeBiRle8(bytes, pixelOffset, pixels, width, height,
                bottomUp, palette!),
            2 => DecodeBiRle4(bytes, pixelOffset, pixels, width, height,
                bottomUp, palette!),
            3 => DecodeBiBitfields(bytes, pixelOffset, pixels, width, height,
                bpp, bottomUp, outBands, maskR, maskG, maskB, maskA),
            _ => false,
        };
        if (!ok) return null;

        return new VipsImage
        {
            Width = width,
            Height = height,
            Bands = outBands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };
    }

    /// <summary>
    /// Decode uncompressed BMP pixel data (BI_RGB) into the row-major
    /// RGB(A) <paramref name="pixels"/> buffer. Handles 1 / 4 / 8 bpp
    /// paletted (palette = BGRA quads), 16 bpp RGB555, 24 bpp BGR, 32 bpp BGRA.
    /// </summary>
    private static bool DecodeBiRgb(byte[] bytes, uint pixelOffset, byte[] pixels,
        int width, int height, ushort bpp, bool bottomUp, int outBands, byte[]? palette)
    {
        int rowStride = ((width * bpp + 31) / 32) * 4;
        if (pixelOffset + (long)rowStride * height > bytes.Length) return false;

        for (int srcRow = 0; srcRow < height; srcRow++)
        {
            int dstRow = bottomUp ? (height - 1 - srcRow) : srcRow;
            int srcOffset = (int)pixelOffset + srcRow * rowStride;
            int dstOffset = dstRow * width * outBands;

            switch (bpp)
            {
                case 1:
                {
                    if (palette == null) return false;
                    for (int x = 0; x < width; x++)
                    {
                        int byteIdx = x >> 3;
                        int bitOff = 7 - (x & 7);
                        int idx = (bytes[srcOffset + byteIdx] >> bitOff) & 1;
                        WritePaletteEntry(pixels, dstOffset + x * 3, palette, idx);
                    }
                    break;
                }
                case 4:
                {
                    if (palette == null) return false;
                    for (int x = 0; x < width; x++)
                    {
                        int byteIdx = x >> 1;
                        int idx = (x & 1) == 0
                            ? (bytes[srcOffset + byteIdx] >> 4) & 0x0F
                            : bytes[srcOffset + byteIdx] & 0x0F;
                        WritePaletteEntry(pixels, dstOffset + x * 3, palette, idx);
                    }
                    break;
                }
                case 8:
                {
                    if (palette == null) return false;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = bytes[srcOffset + x];
                        WritePaletteEntry(pixels, dstOffset + x * 3, palette, idx);
                    }
                    break;
                }
                case 16:
                {
                    // BI_RGB at 16bpp = RGB555 (5R + 5G + 5B, 1 reserved
                    // bit at top). Scale 5-bit channels to 8-bit by
                    // <<3 + replicating MSBs into the low 3 bits.
                    for (int x = 0; x < width; x++)
                    {
                        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(
                            bytes.AsSpan(srcOffset + x * 2, 2));
                        int r5 = (v >> 10) & 0x1F;
                        int g5 = (v >> 5) & 0x1F;
                        int b5 = v & 0x1F;
                        pixels[dstOffset + x * 3 + 0] = (byte)((r5 << 3) | (r5 >> 2));
                        pixels[dstOffset + x * 3 + 1] = (byte)((g5 << 3) | (g5 >> 2));
                        pixels[dstOffset + x * 3 + 2] = (byte)((b5 << 3) | (b5 >> 2));
                    }
                    break;
                }
                case 24:
                {
                    for (int x = 0; x < width; x++)
                    {
                        int sp = srcOffset + x * 3;
                        int dp = dstOffset + x * 3;
                        pixels[dp + 0] = bytes[sp + 2];
                        pixels[dp + 1] = bytes[sp + 1];
                        pixels[dp + 2] = bytes[sp + 0];
                    }
                    break;
                }
                case 32:
                {
                    for (int x = 0; x < width; x++)
                    {
                        int sp = srcOffset + x * 4;
                        int dp = dstOffset + x * 4;
                        pixels[dp + 0] = bytes[sp + 2];
                        pixels[dp + 1] = bytes[sp + 1];
                        pixels[dp + 2] = bytes[sp + 0];
                        pixels[dp + 3] = bytes[sp + 3];
                    }
                    break;
                }
                default:
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Decode BI_RLE8 (run-length-encoded 8bpp paletted). Format:
    ///   pair (count, index): copy the indexed colour count times
    ///   pair (0, 0): end-of-line — pad to start of next row
    ///   pair (0, 1): end-of-bitmap
    ///   pair (0, 2): delta — next 2 bytes are dx, dy offsets
    ///   pair (0, count) where count ≥ 3: absolute mode — next count
    ///     bytes are direct indices, padded to word boundary
    /// </summary>
    private static bool DecodeBiRle8(byte[] bytes, uint pixelOffset, byte[] pixels,
        int width, int height, bool bottomUp, byte[] palette)
    {
        int p = (int)pixelOffset;
        int x = 0, y = 0;
        while (p + 2 <= bytes.Length)
        {
            byte n = bytes[p++];
            byte v = bytes[p++];
            if (n == 0)
            {
                switch (v)
                {
                    case 0:  // end of line
                        x = 0; y++;
                        continue;
                    case 1:  // end of bitmap
                        return true;
                    case 2:  // delta
                        if (p + 2 > bytes.Length) return false;
                        x += bytes[p++];
                        y += bytes[p++];
                        continue;
                    default:  // absolute mode (3..255 raw indices)
                        int count = v;
                        if (p + count > bytes.Length) return false;
                        for (int i = 0; i < count; i++)
                        {
                            if (x < width && y < height)
                                WritePixelAt(pixels, palette, bytes[p + i], width, height, x, y, bottomUp);
                            x++;
                        }
                        p += count;
                        if ((count & 1) == 1) p++;  // pad to word boundary
                        continue;
                }
            }
            // Encoded run: copy palette[v] n times.
            for (int i = 0; i < n; i++)
            {
                if (x < width && y < height)
                    WritePixelAt(pixels, palette, v, width, height, x, y, bottomUp);
                x++;
            }
        }
        return true;
    }

    /// <summary>
    /// Decode BI_RLE4 (run-length-encoded 4bpp paletted). Same control
    /// structure as RLE8 but encoded runs pack two 4-bit indices per
    /// data byte (high nibble first, alternating per pixel), and
    /// absolute mode packs 2 pixels per byte with word-boundary padding.
    /// </summary>
    private static bool DecodeBiRle4(byte[] bytes, uint pixelOffset, byte[] pixels,
        int width, int height, bool bottomUp, byte[] palette)
    {
        int p = (int)pixelOffset;
        int x = 0, y = 0;
        while (p + 2 <= bytes.Length)
        {
            byte n = bytes[p++];
            byte v = bytes[p++];
            if (n == 0)
            {
                switch (v)
                {
                    case 0:
                        x = 0; y++;
                        continue;
                    case 1:
                        return true;
                    case 2:
                        if (p + 2 > bytes.Length) return false;
                        x += bytes[p++];
                        y += bytes[p++];
                        continue;
                    default:
                        // Absolute: `v` 4-bit indices, packed 2 per byte.
                        int count = v;
                        int bytesNeeded = (count + 1) / 2;
                        if (p + bytesNeeded > bytes.Length) return false;
                        for (int i = 0; i < count; i++)
                        {
                            byte b = bytes[p + i / 2];
                            int idx = (i & 1) == 0 ? (b >> 4) : (b & 0x0F);
                            if (x < width && y < height)
                                WritePixelAt(pixels, palette, (byte)idx, width, height, x, y, bottomUp);
                            x++;
                        }
                        p += bytesNeeded;
                        // Pad to word (2-byte) boundary.
                        if ((bytesNeeded & 1) == 1) p++;
                        continue;
                }
            }
            // Encoded run: emit `n` pixels alternating between the
            // high-nibble and low-nibble of `v`.
            int hi = v >> 4;
            int lo = v & 0x0F;
            for (int i = 0; i < n; i++)
            {
                int idx = (i & 1) == 0 ? hi : lo;
                if (x < width && y < height)
                    WritePixelAt(pixels, palette, (byte)idx, width, height, x, y, bottomUp);
                x++;
            }
        }
        return true;
    }

    /// <summary>
    /// Decode BI_BITFIELDS (16- or 32-bpp pixels with caller-supplied
    /// channel masks). The masks describe contiguous bit-fields; we
    /// extract each by shifting down by the mask's lowest set-bit and
    /// rescaling to 8-bit via bit replication when the field is shorter
    /// than 8 bits.
    /// </summary>
    private static bool DecodeBiBitfields(byte[] bytes, uint pixelOffset, byte[] pixels,
        int width, int height, ushort bpp, bool bottomUp, int outBands,
        uint maskR, uint maskG, uint maskB, uint maskA)
    {
        int bytesPerPixel = bpp / 8;
        int rowSize = ((bpp * width + 31) / 32) * 4;
        int p = (int)pixelOffset;

        int shR = TrailingZeros(maskR), wR = PopCount(maskR);
        int shG = TrailingZeros(maskG), wG = PopCount(maskG);
        int shB = TrailingZeros(maskB), wB = PopCount(maskB);
        int shA = maskA != 0 ? TrailingZeros(maskA) : 0;
        int wA = maskA != 0 ? PopCount(maskA) : 0;

        for (int y = 0; y < height; y++)
        {
            if (p + rowSize > bytes.Length) return false;
            int dstY = bottomUp ? (height - 1 - y) : y;
            for (int x = 0; x < width; x++)
            {
                uint v;
                if (bpp == 16) v = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + x * 2, 2));
                else v = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + x * 4, 4));

                byte r = ScaleField((v & maskR) >> shR, wR);
                byte g = ScaleField((v & maskG) >> shG, wG);
                byte b = ScaleField((v & maskB) >> shB, wB);
                int dst = (dstY * width + x) * outBands;
                pixels[dst] = r;
                pixels[dst + 1] = g;
                pixels[dst + 2] = b;
                if (outBands == 4)
                    pixels[dst + 3] = maskA != 0 ? ScaleField((v & maskA) >> shA, wA) : (byte)0xFF;
            }
            p += rowSize;
        }
        return true;
    }

    /// <summary>Scale an n-bit field (n ≤ 8 typically) to 8 bits via bit replication.</summary>
    private static byte ScaleField(uint v, int width)
    {
        if (width <= 0) return 0;
        if (width >= 8) return (byte)(v >> (width - 8));
        // Bit-replicate: e.g., 5-bit 0b10110 → 0b10110_101 = (v << 3) | (v >> 2).
        return (byte)((v << (8 - width)) | (v >> (2 * width - 8 < 0 ? 0 : 2 * width - 8)));
    }

    private static int TrailingZeros(uint v)
    {
        if (v == 0) return 0;
        int n = 0;
        while ((v & 1) == 0) { v >>= 1; n++; }
        return n;
    }

    private static int PopCount(uint v)
    {
        int n = 0;
        while (v != 0) { n += (int)(v & 1); v >>= 1; }
        return n;
    }

    private static void WritePaletteEntry(byte[] pixels, int dst, byte[] palette, int idx)
    {
        int p = idx * 4;
        if (p + 3 > palette.Length) return;
        // Palette is BGRA (A reserved/0); output is RGB.
        pixels[dst + 0] = palette[p + 2];
        pixels[dst + 1] = palette[p + 1];
        pixels[dst + 2] = palette[p + 0];
    }

    private static void WritePixelAt(byte[] pixels, byte[] palette, byte idx,
        int width, int height, int x, int y, bool bottomUp)
    {
        int dstY = bottomUp ? (height - 1 - y) : y;
        if (dstY < 0 || dstY >= height) return;
        int dst = (dstY * width + x) * 3;
        WritePaletteEntry(pixels, dst, palette, idx);
    }

    private static VipsImage? DecodeViaMagick(byte[] imageBytes)
    {
        int width, height, bands;
        try
        {
            using var probe = new MagickImage(imageBytes);
            width = (int)probe.Width;
            height = (int)probe.Height;
            int colorBands = probe.ColorSpace == ColorSpace.Gray ? 1 : 3;
            bands = colorBands + (probe.HasAlpha ? 1 : 0);
        }
        catch
        {
            return null;
        }

        return new VipsImage
        {
            Width = width,
            Height = height,
            Bands = bands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = bands <= 2 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() =>
            {
                using var img = new MagickImage(imageBytes);
                int stride = width * bands;
                var buf = new byte[stride * height];

                if (bands == 1) img.ColorSpace = ColorSpace.Gray;
                else if (bands == 3 && img.HasAlpha) img.Alpha(AlphaOption.Off);
                else if (bands == 4 && !img.HasAlpha) img.Alpha(AlphaOption.On);

                using var pixels = img.GetPixels();
                for (int y = 0; y < height; y++)
                {
                    var row = pixels.GetArea(0, y, (uint)width, 1)
                        ?? throw new InvalidOperationException($"BMP: pixel row {y} returned null");
                    Array.Copy(row, 0, buf, y * stride, stride);
                }
                return buf;
            })
        };
    }

    /// <summary>
    /// Streaming BMP load: feeds the source directly to Magick.NET, decodes
    /// pixels eagerly, and drops the encoded buffer. Trades the laziness of
    /// <see cref="LoadAsync"/> for not holding the encoded BMP alongside the
    /// decoded pixel buffer.
    /// </summary>
    public static async ValueTask<VipsImage?> LoadStreamingAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsBmpAsync(source, cancellationToken)) return null;
        await Task.Yield();

        try
        {
            using var stream = source.AsStream();
            using var img = new MagickImage(stream);

            int width = (int)img.Width;
            int height = (int)img.Height;
            int colorBands = img.ColorSpace == ColorSpace.Gray ? 1 : 3;
            int bands = colorBands + (img.HasAlpha ? 1 : 0);
            int stride = width * bands;
            var buf = new byte[stride * height];

            if (bands == 1) img.ColorSpace = ColorSpace.Gray;
            else if (bands == 3 && img.HasAlpha) img.Alpha(AlphaOption.Off);
            else if (bands == 4 && !img.HasAlpha) img.Alpha(AlphaOption.On);

            using (var pixels = img.GetPixels())
            {
                for (int y = 0; y < height; y++)
                {
                    var row = pixels.GetArea(0, y, (uint)width, 1)
                        ?? throw new InvalidOperationException($"BMP streaming: pixel row {y} returned null");
                    Array.Copy(row, 0, buf, y * stride, stride);
                }
            }

            return new VipsImage
            {
                Width = width,
                Height = height,
                Bands = bands,
                BandFormat = VipsBandFormat.UChar,
                Interpretation = bands <= 2 ? VipsInterpretation.BW : VipsInterpretation.RGB,
                Coding = VipsCoding.None,
                XRes = 1.0,
                YRes = 1.0,
                PixelsLazy = new Lazy<byte[]>(() => buf),
            };
        }
        catch
        {
            return null;
        }
    }
}
