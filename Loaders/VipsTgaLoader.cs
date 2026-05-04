using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

/// <summary>
/// TGA (Truevision TARGA) loader. Pure-C# fast path covers all the
/// commonly-encountered real-world variants:
/// <list type="bullet">
///   <item>Uncompressed paletted (type 1) and RLE paletted (type 9)
///         with 15/16/24/32-bit color map entries</item>
///   <item>Uncompressed RGB (type 2) and RLE RGB (type 10) at depth
///         15/16/24/32 (15/16-bit unpack 5-5-5 BGR with bit replication)</item>
///   <item>Uncompressed grayscale (type 3) and RLE grayscale (type 11)
///         at depth 8</item>
/// </list>
/// Anything outside this set falls back to Magick.NET.
///
/// <para>TGA has no fixed magic at file offset 0. <see cref="IsTgaAsync"/>
/// uses a coarse-but-reliable header validity check (image type ∈
/// known set, colour-map type ∈ {0, 1}, depth ∈ {8, 15, 16, 24, 32})
/// since the alternative — checking the optional "TRUEVISION-XFILE."
/// footer at the file tail — would force a full drain just to detect
/// the format.</para>
/// </summary>
public static class VipsTgaLoader
{
    public static async ValueTask<bool> IsTgaAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(18, cancellationToken);
        if (sniff.Length < 18) return false;
        var s = sniff.Span;
        if (s[1] > 1) return false; // colour-map type ∈ {0, 1}
        byte imageType = s[2];
        if (imageType != 0 && imageType != 1 && imageType != 2 && imageType != 3
            && imageType != 9 && imageType != 10 && imageType != 11) return false;
        byte depth = s[16];
        return depth == 8 || depth == 15 || depth == 16 || depth == 24 || depth == 32;
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsTgaAsync(source, cancellationToken)) return null;

        var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
        }
        var bytes = ms.ToArray();

        var fast = TryDecodePureCSharp(bytes);
        if (fast != null) return fast;

        return await VipsMagickWrapLoader.LoadAsync(
            new PipeVipsSource(System.IO.Pipelines.PipeReader.Create(new MemoryStream(bytes))),
            cancellationToken,
            ImageMagick.MagickFormat.Tga);
    }

    /// <summary>Streaming variant — same eager-buffer shape since TGA fast path needs random access.</summary>
    public static ValueTask<VipsImage?> LoadStreamingAsync(IVipsSource source, CancellationToken cancellationToken = default)
        => LoadAsync(source, cancellationToken);

    /// <summary>
    /// Pure-C# decoder for non-paletted TGAs. Returns <see langword="null"/>
    /// when the image type or depth is outside the supported set, signalling
    /// the caller to fall back to Magick.
    /// </summary>
    private static VipsImage? TryDecodePureCSharp(byte[] bytes)
    {
        if (bytes.Length < 18) return null;

        byte idLength = bytes[0];
        byte colorMapType = bytes[1];
        byte imageType = bytes[2];
        ushort width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12, 2));
        ushort height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14, 2));
        byte depth = bytes[16];
        byte descriptor = bytes[17];

        if (width == 0 || height == 0) return null;
        if (colorMapType > 1) return null;
        if (imageType != 1 && imageType != 2 && imageType != 3
            && imageType != 9 && imageType != 10 && imageType != 11) return null;

        bool isPaletted = imageType == 1 || imageType == 9;
        bool isGrayscale = imageType == 3 || imageType == 11;
        bool isRle = imageType == 9 || imageType == 10 || imageType == 11;

        if (isPaletted && colorMapType != 1) return null;
        if (!isPaletted && colorMapType == 1) return null;  // unexpected palette
        if (isPaletted && depth != 8) return null;
        if (isGrayscale && depth != 8) return null;
        if (!isPaletted && !isGrayscale
            && depth != 15 && depth != 16 && depth != 24 && depth != 32) return null;

        // Image descriptor bit 5: 1 = top-to-bottom, 0 = bottom-to-top.
        bool topDown = (descriptor & 0x20) != 0;

        int colorMapLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(5, 2));
        int colorMapEntrySize = bytes[7];
        int colorMapBytes = isPaletted ? colorMapLength * (colorMapEntrySize / 8) : 0;

        // Output bands: paletted → palette-derived (3 RGB or 4 RGBA);
        // 15/16-bit → 3 RGB (alpha bit dropped); else depth/8.
        int outBands;
        byte[][]? palette = null;
        if (isPaletted)
        {
            if (colorMapEntrySize != 15 && colorMapEntrySize != 16
                && colorMapEntrySize != 24 && colorMapEntrySize != 32) return null;
            outBands = colorMapEntrySize == 32 ? 4 : 3;
            int paletteOff = 18 + idLength;
            if (paletteOff + colorMapBytes > bytes.Length) return null;
            palette = BuildPalette(bytes, paletteOff, colorMapLength, colorMapEntrySize, outBands);
            if (palette == null) return null;
        }
        else if (isGrayscale)
        {
            outBands = 1;
        }
        else
        {
            outBands = depth == 32 ? 4 : 3;
        }

        // Bytes per input pixel (in the encoded stream).
        int inBpp = isPaletted ? 1
            : isGrayscale ? 1
            : (depth == 15 || depth == 16) ? 2
            : depth / 8;

        int pelOffset = 18 + idLength + colorMapBytes;
        if (pelOffset > bytes.Length) return null;

        var pixels = new byte[width * height * outBands];
        int p = pelOffset;

        // Decode straight to OUTPUT layout, with per-pixel translation
        // applied at the point of write. Same path for RLE and raw —
        // RLE just reads control bytes first; both eventually call
        // TranslatePixel which handles palette / RGB555 / BGR(A) translation.
        int total = width * height;
        Span<byte> px = stackalloc byte[4];
        if (isRle)
        {
            int produced = 0;
            while (produced < total)
            {
                if (p >= bytes.Length) return null;
                byte ctrl = bytes[p++];
                int count = (ctrl & 0x7F) + 1;
                if (produced + count > total) return null;

                if ((ctrl & 0x80) != 0)
                {
                    // Repeat a single source pixel `count` times.
                    if (p + inBpp > bytes.Length) return null;
                    int outBytes = TranslatePixel(bytes, p, inBpp, depth, isGrayscale,
                        isPaletted, palette, px);
                    if (outBytes < 0) return null;
                    p += inBpp;
                    for (int i = 0; i < count; i++)
                    {
                        int dst = (produced + i) * outBands;
                        for (int b = 0; b < outBands; b++) pixels[dst + b] = px[b];
                    }
                }
                else
                {
                    // `count` distinct source pixels follow.
                    if (p + count * inBpp > bytes.Length) return null;
                    for (int i = 0; i < count; i++)
                    {
                        int outBytes = TranslatePixel(bytes, p, inBpp, depth, isGrayscale,
                            isPaletted, palette, px);
                        if (outBytes < 0) return null;
                        p += inBpp;
                        int dst = (produced + i) * outBands;
                        for (int b = 0; b < outBands; b++) pixels[dst + b] = px[b];
                    }
                }
                produced += count;
            }
        }
        else
        {
            if (p + total * inBpp > bytes.Length) return null;
            for (int i = 0; i < total; i++)
            {
                int outBytes = TranslatePixel(bytes, p, inBpp, depth, isGrayscale,
                    isPaletted, palette, px);
                if (outBytes < 0) return null;
                p += inBpp;
                int dst = i * outBands;
                for (int b = 0; b < outBands; b++) pixels[dst + b] = px[b];
            }
        }

        // Flip vertically when the file is bottom-up.
        if (!topDown)
        {
            int rowBytes = width * outBands;
            var tmp = new byte[rowBytes];
            for (int y = 0; y < height / 2; y++)
            {
                int top = y * rowBytes;
                int bot = (height - 1 - y) * rowBytes;
                Buffer.BlockCopy(pixels, top, tmp, 0, rowBytes);
                Buffer.BlockCopy(pixels, bot, pixels, top, rowBytes);
                Buffer.BlockCopy(tmp, 0, pixels, bot, rowBytes);
            }
        }

        return new VipsImage
        {
            Width = width,
            Height = height,
            Bands = outBands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = isGrayscale ? VipsInterpretation.BW : VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };
    }

    /// <summary>
    /// Translate one input pixel into RGB/RGBA/Gray bytes in <paramref name="dst"/>.
    /// Returns the number of output bytes written (= outBands), or -1 if
    /// the input is malformed (palette index out of range).
    /// </summary>
    private static int TranslatePixel(byte[] src, int offset, int inBpp, int depth,
        bool isGrayscale, bool isPaletted, byte[][]? palette, Span<byte> dst)
    {
        if (isGrayscale)
        {
            dst[0] = src[offset];
            return 1;
        }
        if (isPaletted)
        {
            int idx = src[offset];
            if (idx >= palette!.Length) return -1;
            var entry = palette[idx];
            for (int i = 0; i < entry.Length; i++) dst[i] = entry[i];
            return entry.Length;
        }
        if (depth == 15 || depth == 16)
        {
            // 16-bit BGR555: ARRRRR-GGGGG-BBBBB stored little-endian.
            ushort v = (ushort)(src[offset] | (src[offset + 1] << 8));
            int r = (v >> 10) & 0x1F;
            int g = (v >> 5) & 0x1F;
            int b = v & 0x1F;
            // Bit-replicate 5→8 (so 0x1F maps to 0xFF, not 0xF8).
            dst[0] = (byte)((r << 3) | (r >> 2));
            dst[1] = (byte)((g << 3) | (g >> 2));
            dst[2] = (byte)((b << 3) | (b >> 2));
            return 3;
        }
        // 24/32-bit BGR(A) → RGB(A).
        dst[0] = src[offset + 2];
        dst[1] = src[offset + 1];
        dst[2] = src[offset + 0];
        if (inBpp == 4) { dst[3] = src[offset + 3]; return 4; }
        return 3;
    }

    private static byte[][]? BuildPalette(byte[] bytes, int offset, int length, int entryBits, int outBands)
    {
        int entryBytes = entryBits / 8;
        var palette = new byte[length][];
        for (int i = 0; i < length; i++)
        {
            int sp = offset + i * entryBytes;
            switch (entryBits)
            {
                case 24:
                    palette[i] = new[] { bytes[sp + 2], bytes[sp + 1], bytes[sp + 0] };
                    break;
                case 32:
                    palette[i] = new[] { bytes[sp + 2], bytes[sp + 1], bytes[sp + 0], bytes[sp + 3] };
                    break;
                case 15:
                case 16:
                {
                    ushort v = (ushort)(bytes[sp] | (bytes[sp + 1] << 8));
                    int r = (v >> 10) & 0x1F;
                    int g = (v >> 5) & 0x1F;
                    int b = v & 0x1F;
                    palette[i] = new[]
                    {
                        (byte)((r << 3) | (r >> 2)),
                        (byte)((g << 3) | (g >> 2)),
                        (byte)((b << 3) | (b >> 2)),
                    };
                    break;
                }
                default: return null;
            }
        }
        return palette;
    }
}
