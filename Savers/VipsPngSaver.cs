using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Hashing;
using ImageMagick;

namespace CosmoImage.Savers;

public static class VipsPngSaver
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// Write the image as PNG. When <paramref name="palette"/> is set
    /// (range 2..256), output is palette-indexed PNG-8 with N colors,
    /// quantized + dithered via Magick.NET. Otherwise the image is written
    /// as full-color PNG via the direct deflate path.
    /// </summary>
    public static async Task SaveAsync(VipsImage image, PipeWriter writer, int? palette = null, CancellationToken cancellationToken = default)
    {
        if (palette.HasValue)
        {
            await SavePalettePngAsync(image, writer, palette.Value, cancellationToken);
            return;
        }

        var stream = writer.AsStream();
        
        // 1. Signature
        await stream.WriteAsync(PngSignature, cancellationToken);

        // 2. IHDR
        byte colorType = image.Bands switch
        {
            1 => (byte)0, // Grayscale
            2 => (byte)4, // Grayscale + Alpha
            3 => (byte)2, // RGB
            4 => (byte)6, // RGBA
            _ => throw new NotSupportedException($"Unsupported band count: {image.Bands}")
        };

        byte[] ihdrData = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdrData.AsSpan(0, 4), image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdrData.AsSpan(4, 4), image.Height);
        ihdrData[8] = 8; // Bit depth
        ihdrData[9] = colorType;
        ihdrData[10] = 0; // Compression (Deflate)
        ihdrData[11] = 0; // Filter (None)
        ihdrData[12] = 0; // Interlace (None)

        await WriteChunkAsync(stream, "IHDR", ihdrData, cancellationToken);

        // Optional metadata chunks. iCCP must come before IDAT per PNG spec;
        // eXIf is unrestricted but conventionally written before IDAT too.
        if (image.MetadataBlobs.TryGetValue("icc", out var iccBlob))
        {
            await WriteChunkAsync(stream, "iCCP", BuildIccpData(iccBlob), cancellationToken);
        }
        if (image.MetadataBlobs.TryGetValue("exif", out var exifBlob))
        {
            await WriteChunkAsync(stream, "eXIf", exifBlob, cancellationToken);
        }

        // 3. IDAT (Compressed pixels)
        // Pixel preparation runs in parallel across strip-shaped tiles via VipsSink;
        // the reorder buffer in OrderedStripSink ensures rows reach the deflate stream
        // strictly top-down, which PNG (and ZLibStream) requires.
        using (var idatMs = new MemoryStream())
        {
            using (var zlib = new ZLibStream(idatMs, CompressionLevel.Optimal, true))
            {
                int pelSize = image.SizeOfPel;
                int rowWidth = image.Width * pelSize;

                var sink = new OrderedStripSink(image, tileHeight: 16, (top, height, bytes) =>
                {
                    for (int row = 0; row < height; row++)
                    {
                        zlib.WriteByte(0); // Filter type 0 (None)
                        zlib.Write(bytes.AsSpan(row * rowWidth, rowWidth));
                    }
                });
                await sink.RunAsync(cancellationToken);
            }
            await WriteChunkAsync(stream, "IDAT", idatMs.ToArray(), cancellationToken);
        }

        // 4. IEND
        await WriteChunkAsync(stream, "IEND", Array.Empty<byte>(), cancellationToken);

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    /// <summary>
    /// Palette-indexed PNG-8 path. Routes through Magick.NET, which handles
    /// quantization + palette construction + index encoding. Metadata profiles
    /// round-trip via Magick's profile API.
    /// </summary>
    private static async Task SavePalettePngAsync(VipsImage image, PipeWriter writer, int colors, CancellationToken cancellationToken)
    {
        if (colors < 2 || colors > 256)
            throw new ArgumentOutOfRangeException(nameof(colors), "Palette PNG must have 2..256 colors.");

        int width = image.Width;
        int height = image.Height;
        int bands = image.Bands;
        if (bands != 1 && bands != 3 && bands != 4)
            throw new NotSupportedException($"Palette PNG save needs 1, 3, or 4 bands; got {bands}");

        byte[] pixels;
        if (image.Pixels is { } existing)
        {
            pixels = existing;
        }
        else
        {
            var sink = new MemorySink(image);
            await sink.RunAsync(cancellationToken);
            pixels = sink.Pixels;
        }

        var rawFormat = bands switch
        {
            1 => MagickFormat.Gray,
            3 => MagickFormat.Rgb,
            4 => MagickFormat.Rgba,
            _ => throw new InvalidOperationException()
        };

        var readSettings = new MagickReadSettings
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = rawFormat,
            Depth = 8,
        };

        using var magickImage = new MagickImage();
        magickImage.Read(pixels, readSettings);

        magickImage.Quantize(new QuantizeSettings
        {
            Colors = (uint)colors,
            DitherMethod = DitherMethod.FloydSteinberg,
        });

        // Png8 = palette-indexed PNG. Magick handles PLTE/tRNS/IDAT generation.
        magickImage.Format = MagickFormat.Png8;

        if (image.MetadataBlobs.TryGetValue("exif", out var exif))
            magickImage.SetProfile(new ImageProfile("exif", exif));
        if (image.MetadataBlobs.TryGetValue("xmp", out var xmp))
            magickImage.SetProfile(new ImageProfile("xmp", xmp));
        if (image.MetadataBlobs.TryGetValue("icc", out var icc))
            magickImage.SetProfile(new ColorProfile(icc));

        using var ms = new MemoryStream();
        magickImage.Write(ms);
        ms.Position = 0;
        await ms.CopyToAsync(writer.AsStream(), cancellationToken);

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    /// <summary>
    /// Build the iCCP chunk payload: profile name (≤79 ASCII chars) + 0x00
    /// terminator + compression method byte (0 = deflate) + deflate-compressed
    /// ICC profile bytes.
    /// </summary>
    private static byte[] BuildIccpData(byte[] iccProfile)
    {
        // Profile name is required by spec (1-79 chars) but no consumer cares.
        var name = System.Text.Encoding.ASCII.GetBytes("ICC");
        using var ms = new MemoryStream();
        ms.Write(name, 0, name.Length);
        ms.WriteByte(0);  // null terminator
        ms.WriteByte(0);  // compression method = deflate
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(iccProfile, 0, iccProfile.Length);
        }
        return ms.ToArray();
    }

    private static async Task WriteChunkAsync(Stream stream, string type, byte[] data, CancellationToken cancellationToken)
    {
        byte[] lengthBuf = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBuf, data.Length);
        await stream.WriteAsync(lengthBuf, cancellationToken);

        byte[] typeBuf = System.Text.Encoding.ASCII.GetBytes(type);
        await stream.WriteAsync(typeBuf, cancellationToken);

        if (data.Length > 0)
        {
            await stream.WriteAsync(data, cancellationToken);
        }

        var crc = new Crc32();
        crc.Append(typeBuf);
        crc.Append(data);
        
        byte[] crcBuf = new byte[4];
        crc.GetCurrentHash(crcBuf);
        
        // Crc32.GetCurrentHash returns the result in little-endian order.
        // PNG requires big-endian.
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(crcBuf);
        }
        
        await stream.WriteAsync(crcBuf, cancellationToken);
    }
}
