using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

/// <summary>
/// TGA (Truevision TARGA) loader. Pure-C# fast path for the common
/// non-paletted variants: uncompressed RGB (type 2, 24/32 bpp),
/// uncompressed grayscale (type 3, 8 bpp), and their RLE-compressed
/// counterparts (types 10/11). Paletted variants (types 1/9), 16 bpp
/// RGB555, and any unrecognised image-type byte still go through
/// Magick.NET — the fast path covers ~all real-world modern TGAs while
/// preserving full coverage for edge files.
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
        // colorMap spec at bytes[3..7]; we skip past it for non-paletted types.
        ushort width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12, 2));
        ushort height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14, 2));
        byte depth = bytes[16];
        byte descriptor = bytes[17];

        if (width == 0 || height == 0) return null;
        if (colorMapType != 0) return null; // paletted → Magick
        if (imageType != 2 && imageType != 3 && imageType != 10 && imageType != 11) return null;
        bool isGrayscale = imageType == 3 || imageType == 11;
        bool isRle = imageType == 10 || imageType == 11;

        if (isGrayscale && depth != 8) return null;
        if (!isGrayscale && depth != 24 && depth != 32) return null;

        // Image descriptor bit 5: 1 = top-to-bottom, 0 = bottom-to-top.
        // Bit 4 (left-to-right) is universally 0 in real-world files; we
        // ignore the right-to-left bit and treat it as left-to-right.
        bool topDown = (descriptor & 0x20) != 0;

        int bands = depth / 8; // 1, 3, or 4
        int pelOffset = 18 + idLength;
        // colour-map length × entry size — 0 for non-paletted but spec
        // technically requires reading past the (empty) colour-map block.
        int colorMapLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(5, 2));
        int colorMapEntrySize = bytes[7];
        pelOffset += colorMapLength * (colorMapEntrySize / 8);
        if (pelOffset > bytes.Length) return null;

        var pixels = new byte[width * height * bands];
        int p = pelOffset;

        if (isRle)
        {
            int produced = 0;
            int total = width * height;
            while (produced < total)
            {
                if (p >= bytes.Length) return null;
                byte ctrl = bytes[p++];
                int count = (ctrl & 0x7F) + 1;
                if (produced + count > total) return null;

                if ((ctrl & 0x80) != 0)
                {
                    // RLE packet — single pixel value repeated `count` times.
                    if (p + bands > bytes.Length) return null;
                    if (isGrayscale)
                    {
                        byte v = bytes[p++];
                        for (int i = 0; i < count; i++)
                            pixels[(produced + i)] = v;
                    }
                    else
                    {
                        byte b = bytes[p++], g = bytes[p++], r = bytes[p++];
                        byte a = bands == 4 ? bytes[p++] : (byte)0;
                        for (int i = 0; i < count; i++)
                        {
                            int dst = (produced + i) * bands;
                            pixels[dst + 0] = r;
                            pixels[dst + 1] = g;
                            pixels[dst + 2] = b;
                            if (bands == 4) pixels[dst + 3] = a;
                        }
                    }
                }
                else
                {
                    // Literal packet — `count` distinct pixels follow.
                    if (p + count * bands > bytes.Length) return null;
                    if (isGrayscale)
                    {
                        for (int i = 0; i < count; i++)
                            pixels[produced + i] = bytes[p++];
                    }
                    else
                    {
                        for (int i = 0; i < count; i++)
                        {
                            byte b = bytes[p++], g = bytes[p++], r = bytes[p++];
                            byte a = bands == 4 ? bytes[p++] : (byte)0;
                            int dst = (produced + i) * bands;
                            pixels[dst + 0] = r;
                            pixels[dst + 1] = g;
                            pixels[dst + 2] = b;
                            if (bands == 4) pixels[dst + 3] = a;
                        }
                    }
                }
                produced += count;
            }
        }
        else
        {
            // Uncompressed: contiguous pixel data.
            int dataLen = width * height * bands;
            if (p + dataLen > bytes.Length) return null;
            if (isGrayscale)
            {
                Buffer.BlockCopy(bytes, p, pixels, 0, dataLen);
            }
            else
            {
                for (int i = 0; i < width * height; i++)
                {
                    int sp = p + i * bands;
                    int dp = i * bands;
                    pixels[dp + 0] = bytes[sp + 2]; // R from B-position
                    pixels[dp + 1] = bytes[sp + 1];
                    pixels[dp + 2] = bytes[sp + 0]; // B from R-position
                    if (bands == 4) pixels[dp + 3] = bytes[sp + 3];
                }
            }
        }

        // Flip vertically when the file is bottom-up. Faster as a swap-pass
        // on the assembled buffer than threading a flag through the decode
        // loop, especially for RLE where the loop is already complex.
        if (!topDown)
        {
            int rowBytes = width * bands;
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
            Bands = bands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = isGrayscale ? VipsInterpretation.BW : VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };
    }
}
