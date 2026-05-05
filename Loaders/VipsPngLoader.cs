using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StbImageSharp;

namespace CosmoImage.Loaders;

public static class VipsPngLoader
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static async ValueTask<bool> IsPngAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(8, cancellationToken);
        if (sniff.Length < 8) return false;
        
        return sniff.Span.SequenceEqual(PngSignature);
    }

    public static async ValueTask<VipsImage?> LoadHeaderAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsPngAsync(source, cancellationToken))
            return null;

        // Skip signature
        var signatureBuffer = new byte[8];
        await source.ReadAsync(signatureBuffer, cancellationToken);

        VipsImage? image = null;
        byte colorType = 0;

        var buffer = new byte[8]; // Length (4) + Type (4)
        while (true)
        {
            var readCount = await source.ReadAsync(buffer, cancellationToken);
            if (readCount < 8) break;

            uint length = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0, 4));
            uint type = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4, 4));

            if (type == 0x49484452) // "IHDR"
            {
                var ihdrBuffer = new byte[13];
                int ihdrRead = await source.ReadAsync(ihdrBuffer, cancellationToken);
                if (ihdrRead < 13) break;

                int width = BinaryPrimitives.ReadInt32BigEndian(ihdrBuffer.AsSpan(0, 4));
                int height = BinaryPrimitives.ReadInt32BigEndian(ihdrBuffer.AsSpan(4, 4));
                byte bitDepth = ihdrBuffer[8];
                colorType = ihdrBuffer[9];

                int bands = colorType switch
                {
                    0 => 1, // Grayscale
                    2 => 3, // RGB
                    3 => 3, // Palette (expanded to RGB)
                    4 => 2, // Grayscale + Alpha
                    6 => 4, // RGB + Alpha
                    _ => 0
                };

                image = new VipsImage
                {
                    Width = width,
                    Height = height,
                    Bands = bands,
                    BandFormat = bitDepth > 8 ? VipsBandFormat.UShort : VipsBandFormat.UChar,
                    Interpretation = bands <= 2 ? VipsInterpretation.BW : VipsInterpretation.RGB,
                    Coding = VipsCoding.None,
                    XRes = 1.0,
                    YRes = 1.0
                };

                // Skip CRC (4)
                await source.ReadAsync(new byte[4], cancellationToken);
            }
            else if (type == 0x49454E44) // "IEND"
            {
                return image;
            }
            else if (type == 0x74524E53 && image != null) // "tRNS"
            {
                bool hasAlpha = (colorType & 4) != 0;
                if (!hasAlpha)
                {
                    image.Bands++;
                    image.Interpretation = image.Bands == 2 ? VipsInterpretation.BW : VipsInterpretation.RGB;
                }
                await SkipAsync(source, (long)length + 4, cancellationToken);
            }
            else if (type == 0x70485973 && image != null) // "pHYs"
            {
                var physBuffer = new byte[9];
                int physRead = await source.ReadAsync(physBuffer, cancellationToken);
                if (physRead == 9)
                {
                    int ppuX = BinaryPrimitives.ReadInt32BigEndian(physBuffer.AsSpan(0, 4));
                    int ppuY = BinaryPrimitives.ReadInt32BigEndian(physBuffer.AsSpan(4, 4));
                    byte unit = physBuffer[8];
                    if (unit == 1) // Meters
                    {
                        image.XRes = ppuX / 1000.0;
                        image.YRes = ppuY / 1000.0;
                    }
                }
                await SkipAsync(source, (long)length - physRead + 4, cancellationToken);
            }
            else if (type == 0x65584966 && image != null) // "eXIf" — raw TIFF EXIF
            {
                var data = new byte[length];
                int read = await source.ReadAsync(data, cancellationToken);
                if (read == (int)length)
                    image.MetadataBlobs["exif"] = data;
                await SkipAsync(source, 4, cancellationToken); // CRC
            }
            else if (type == 0x69545874 && image != null) // "iTXt" — internationalized text; XMP rides here
            {
                var data = new byte[length];
                int read = await source.ReadAsync(data, cancellationToken);
                if (read == (int)length)
                {
                    var xmp = TryExtractXmpFromITxt(data);
                    if (xmp != null) image.MetadataBlobs["xmp"] = xmp;
                }
                await SkipAsync(source, 4, cancellationToken); // CRC
            }
            else if (type == 0x69434350 && image != null) // "iCCP" — name + 0 + compression(0) + deflate(profile)
            {
                var data = new byte[length];
                int read = await source.ReadAsync(data, cancellationToken);
                if (read == (int)length)
                {
                    int nameEnd = Array.IndexOf(data, (byte)0);
                    if (nameEnd >= 0 && nameEnd + 2 <= data.Length && data[nameEnd + 1] == 0)
                    {
                        // Inflate the deflate-compressed profile that follows.
                        try
                        {
                            using var compressed = new MemoryStream(data, nameEnd + 2, data.Length - nameEnd - 2);
                            using var deflate = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionMode.Decompress);
                            using var output = new MemoryStream();
                            await deflate.CopyToAsync(output, cancellationToken);
                            image.MetadataBlobs["icc"] = output.ToArray();
                        }
                        catch { /* malformed iCCP — skip silently */ }
                    }
                }
                await SkipAsync(source, 4, cancellationToken); // CRC
            }
            else
            {
                await SkipAsync(source, (long)length + 4, cancellationToken);
            }
        }

        return image;
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int readCount = await source.ReadAsync(buffer, cancellationToken);
            if (readCount == 0) break;
            ms.Write(buffer, 0, readCount);
        }

        var imageBytes = ms.ToArray();
        var memSource = new PipeVipsSource(System.IO.Pipelines.PipeReader.Create(new MemoryStream(imageBytes)));
        var image = await LoadHeaderAsync(memSource, cancellationToken);
        if (image == null) return null;

        // APNG detection — if the stream has an acTL chunk and the
        // pure decoder can handle it, switch to multi-frame mode.
        var apng = PureApngDecoder.TryDecode(imageBytes);
        if (apng != null)
        {
            // Stack frames vertically — same convention as animated
            // GIF / WebP / HEIF in this codebase.
            image.Width = apng.CanvasWidth;
            image.Height = apng.CanvasHeight * apng.FrameCount;
            image.Bands = 4;
            image.BandFormat = VipsBandFormat.UChar;
            image.Interpretation = VipsInterpretation.RGB;
            image.Metadata["n-pages"] = apng.FrameCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            image.Metadata["page-height"] = apng.CanvasHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
            image.Metadata["animation-delays"] = string.Join(",", apng.DelaysCentiseconds);
            var apngPixels = apng.Pixels;
            image.PixelsLazy = new Lazy<byte[]>(() => apngPixels);
            return image;
        }

        image.PixelsLazy = new Lazy<byte[]>(() =>
        {
            // Try the pure-managed decoder first (handles 8/16-bit,
            // Adam7 interlace, color types 0/2/3/4/6, tRNS expansion).
            // Fall back to StbImageSharp for malformed or out-of-spec
            // streams that the pure path bails on.
            var pure = PurePngDecoder.TryDecode(imageBytes, out _);
            if (pure != null) return pure;
            var result = ImageResult.FromMemory(imageBytes, ColorComponents.Default)
                ?? throw new InvalidOperationException("PNG decode failed");
            return result.Data;
        });

        return image;
    }

    /// <summary>
    /// Parse the PNG iTXt chunk payload and return the embedded text bytes if
    /// the keyword identifies an XMP packet. iTXt layout (PNG spec 11.3.4.5):
    /// keyword (1-79 latin-1) + 0x00 + compression flag (1) + compression
    /// method (1) + language tag + 0x00 + translated keyword + 0x00 +
    /// text (UTF-8, deflate-compressed iff flag=1). We accept the canonical
    /// Adobe XMP keyword "XML:com.adobe.xmp".
    /// </summary>
    private static byte[]? TryExtractXmpFromITxt(byte[] data)
    {
        try
        {
            int kEnd = Array.IndexOf(data, (byte)0);
            if (kEnd <= 0 || kEnd + 4 >= data.Length) return null;
            string keyword = System.Text.Encoding.Latin1.GetString(data, 0, kEnd);
            if (keyword != "XML:com.adobe.xmp") return null;

            int p = kEnd + 1;
            byte compFlag = data[p++];
            p++; // compression method (always 0)
            int langEnd = Array.IndexOf(data, (byte)0, p);
            if (langEnd < 0) return null;
            int trEnd = Array.IndexOf(data, (byte)0, langEnd + 1);
            if (trEnd < 0) return null;

            int textStart = trEnd + 1;
            int textLen = data.Length - textStart;
            if (textLen <= 0) return null;

            if (compFlag == 0)
            {
                var raw = new byte[textLen];
                Buffer.BlockCopy(data, textStart, raw, 0, textLen);
                return raw;
            }
            using var compressed = new MemoryStream(data, textStart, textLen);
            using var inflate = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflate.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static async Task SkipAsync(IVipsSource source, long length, CancellationToken cancellationToken)
    {
        if (length <= 0) return;
        var skipBuffer = new byte[Math.Min(length, 4096)];
        while (length > 0)
        {
            int toRead = (int)Math.Min(length, skipBuffer.Length);
            int read = await source.ReadAsync(skipBuffer.AsMemory(0, toRead), cancellationToken);
            if (read <= 0) break;
            length -= read;
        }
    }
}
