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
        // Only BITMAPINFOHEADER for the fast path. V4/V5 sometimes use
        // identical pixel layout but the extra fields complicate validation.
        if (dibSize != 40) return null;

        int width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18, 4));
        int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22, 4));
        ushort planes = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26, 2));
        ushort bpp = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2));
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(30, 4));

        if (planes != 1) return null;
        if (compression != 0 /* BI_RGB */) return null;
        if (bpp != 24 && bpp != 32) return null;
        if (width <= 0) return null;

        int height = Math.Abs(rawHeight);
        if (height == 0) return null;
        // Positive height: rows stored bottom-to-top in the file. Negative:
        // top-to-bottom. We always flip to top-to-bottom in the output buffer.
        bool bottomUp = rawHeight > 0;

        int bytesPerPixel = bpp / 8;
        int bands = bytesPerPixel; // 24 → 3 RGB; 32 → 4 RGBA.
        // BMP rows are padded to 4-byte boundary.
        int rowStride = ((width * bpp + 31) / 32) * 4;
        long pixelDataSize = (long)rowStride * height;
        if (pixelOffset + pixelDataSize > bytes.Length) return null;

        var pixels = new byte[width * height * bands];
        for (int srcRow = 0; srcRow < height; srcRow++)
        {
            int dstRow = bottomUp ? (height - 1 - srcRow) : srcRow;
            int srcOffset = (int)pixelOffset + srcRow * rowStride;
            int dstOffset = dstRow * width * bands;

            for (int x = 0; x < width; x++)
            {
                int sp = srcOffset + x * bytesPerPixel;
                int dp = dstOffset + x * bands;
                // BMP pixel order: BGR(A). Convert to RGB(A) by swapping
                // B↔R; alpha (if present) passes through.
                pixels[dp + 0] = bytes[sp + 2];
                pixels[dp + 1] = bytes[sp + 1];
                pixels[dp + 2] = bytes[sp + 0];
                if (bands == 4) pixels[dp + 3] = bytes[sp + 3];
            }
        }

        return new VipsImage
        {
            Width = width,
            Height = height,
            Bands = bands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };
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
