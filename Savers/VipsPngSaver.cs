using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Hashing;

namespace CosmoImage.Savers;

public static class VipsPngSaver
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// Write the image as PNG. When <paramref name="palette"/> is set
    /// (range 2..256), output is palette-indexed PNG-8 with N colors,
    /// quantized + dithered via the pure-C# octree quantizer. Otherwise
    /// the image is written as full-color PNG via the direct deflate path.
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
        if (image.MetadataBlobs.TryGetValue("xmp", out var xmpBlob))
        {
            await WriteChunkAsync(stream, "iTXt", BuildXmpItxtData(xmpBlob), cancellationToken);
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
                        zlib.Write(bytes.Slice(row * rowWidth, rowWidth));
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
    /// Palette-indexed PNG-8 path. Pure-C#: quantize via
    /// <see cref="VipsOctreeQuantizer"/> to get ≤<paramref name="colors"/>
    /// unique colours, then walk the quantized pixels building a
    /// (RGBA → index) palette map and an indexed pixel buffer. Emit
    /// <c>IHDR (CT=3)</c> + <c>PLTE</c> + optional <c>tRNS</c> +
    /// <c>IDAT(indices)</c>. iCCP / eXIf / iTXt metadata chunks pass
    /// through unchanged.
    /// </summary>
    private static async Task SavePalettePngAsync(VipsImage image, PipeWriter writer, int colors, CancellationToken cancellationToken)
    {
        if (colors < 2 || colors > 256)
            throw new ArgumentOutOfRangeException(nameof(colors), "Palette PNG must have 2..256 colors.");

        int width = image.Width;
        int height = image.Height;
        int bands = image.Bands;
        if (bands != 1 && bands != 2 && bands != 3 && bands != 4)
            throw new NotSupportedException($"Palette PNG save needs 1-4 bands; got {bands}");

        // Quantize via the pure octree quantizer. Output stays in the
        // same direct-color shape as the input (1/2/3/4 band UChar) but
        // with at most `colors` distinct values across the canvas.
        var quantizer = new CosmoImage.Operations.Misc.VipsOctreeQuantizer { Colors = colors };
        var quantized = quantizer.Apply(image);

        byte[] pixels;
        if (quantized.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(quantized);
            await sink.RunAsync(cancellationToken);
            pixels = sink.Pixels;
        }

        // Build palette + index buffer from the quantized pixels.
        // Greyscale / greyscale+alpha get expanded into the palette as
        // R=G=B=greyscale; PNG palette PNGs only have one form (CT 3).
        bool hasAlpha = bands == 2 || bands == 4;
        var paletteMap = new System.Collections.Generic.Dictionary<uint, int>();
        var paletteR = new System.Collections.Generic.List<byte>(colors);
        var paletteG = new System.Collections.Generic.List<byte>(colors);
        var paletteB = new System.Collections.Generic.List<byte>(colors);
        var paletteA = new System.Collections.Generic.List<byte>(colors);
        var indices = new byte[width * height];
        for (int i = 0; i < width * height; i++)
        {
            byte r, g, b, a;
            int srcOff = i * bands;
            switch (bands)
            {
                case 1: r = g = b = pixels[srcOff]; a = 255; break;
                case 2: r = g = b = pixels[srcOff]; a = pixels[srcOff + 1]; break;
                case 3: r = pixels[srcOff]; g = pixels[srcOff + 1]; b = pixels[srcOff + 2]; a = 255; break;
                default: r = pixels[srcOff]; g = pixels[srcOff + 1]; b = pixels[srcOff + 2]; a = pixels[srcOff + 3]; break;
            }
            uint key = ((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a;
            if (!paletteMap.TryGetValue(key, out int idx))
            {
                idx = paletteR.Count;
                if (idx >= 256) throw new InvalidOperationException("Quantizer produced more than 256 distinct palette entries");
                paletteMap[key] = idx;
                paletteR.Add(r); paletteG.Add(g); paletteB.Add(b); paletteA.Add(a);
            }
            indices[i] = (byte)idx;
        }

        var stream = writer.AsStream();
        await stream.WriteAsync(PngSignature, cancellationToken);

        // IHDR — palette mode, 8-bit indices.
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;
        ihdr[9] = 3; // CT 3 = palette
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        await WriteChunkAsync(stream, "IHDR", ihdr, cancellationToken);

        // Metadata chunks — same chunks the full-color path emits.
        if (image.MetadataBlobs.TryGetValue("icc", out var iccBlob))
            await WriteChunkAsync(stream, "iCCP", BuildIccpData(iccBlob), cancellationToken);
        if (image.MetadataBlobs.TryGetValue("exif", out var exifBlob))
            await WriteChunkAsync(stream, "eXIf", exifBlob, cancellationToken);
        if (image.MetadataBlobs.TryGetValue("xmp", out var xmpBlob))
            await WriteChunkAsync(stream, "iTXt", BuildXmpItxtData(xmpBlob), cancellationToken);

        // PLTE — 3 bytes per entry.
        var plte = new byte[paletteR.Count * 3];
        for (int i = 0; i < paletteR.Count; i++)
        {
            plte[i * 3 + 0] = paletteR[i];
            plte[i * 3 + 1] = paletteG[i];
            plte[i * 3 + 2] = paletteB[i];
        }
        await WriteChunkAsync(stream, "PLTE", plte, cancellationToken);

        // tRNS — only when input had alpha; one byte per palette entry.
        // Optimization: if all alphas are 255, skip tRNS entirely.
        if (hasAlpha)
        {
            bool allOpaque = true;
            for (int i = 0; i < paletteA.Count; i++) if (paletteA[i] != 255) { allOpaque = false; break; }
            if (!allOpaque)
            {
                // Trim trailing-255 entries — common optimization, since
                // tRNS is implicitly 255 for indices past its length.
                int trnsLen = paletteA.Count;
                while (trnsLen > 0 && paletteA[trnsLen - 1] == 255) trnsLen--;
                var trns = new byte[trnsLen];
                for (int i = 0; i < trnsLen; i++) trns[i] = paletteA[i];
                await WriteChunkAsync(stream, "tRNS", trns, cancellationToken);
            }
        }

        // IDAT — filter byte (None) per row + indices, zlib-compressed.
        using var idat = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(indices, y * width, width);
            }
        }
        await WriteChunkAsync(stream, "IDAT", idat.ToArray(), cancellationToken);

        await WriteChunkAsync(stream, "IEND", Array.Empty<byte>(), cancellationToken);

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

    /// <summary>
    /// Build an iTXt chunk payload for an XMP packet. Layout:
    /// "XML:com.adobe.xmp" + 0x00 + compression flag (0 = uncompressed) +
    /// compression method (0) + empty language tag + 0x00 + empty translated
    /// keyword + 0x00 + UTF-8 XMP text. Uncompressed is the convention every
    /// XMP-aware reader recognizes.
    /// </summary>
    private static byte[] BuildXmpItxtData(byte[] xmp)
    {
        var keyword = System.Text.Encoding.Latin1.GetBytes("XML:com.adobe.xmp");
        using var ms = new MemoryStream();
        ms.Write(keyword, 0, keyword.Length);
        ms.WriteByte(0);  // null terminator after keyword
        ms.WriteByte(0);  // compression flag (uncompressed)
        ms.WriteByte(0);  // compression method
        ms.WriteByte(0);  // empty language tag + null
        ms.WriteByte(0);  // empty translated keyword + null
        ms.Write(xmp, 0, xmp.Length);
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
