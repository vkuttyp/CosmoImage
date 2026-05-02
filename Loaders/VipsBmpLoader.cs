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
    /// Full BMP load with pixel data. Goes through Magick.NET — BMP variants
    /// (BITMAPCOREHEADER through V5, RLE-compressed paletted, 16/24/32 bpp,
    /// alpha bit-fields) all decode uniformly there.
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
