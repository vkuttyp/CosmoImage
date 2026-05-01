using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CosmoImage.Loaders;

public static class VipsHeifLoader
{
    private static readonly string[] HeifBrands = { "heic", "heix", "hevc", "heim", "heis", "hevm", "hevs", "mif1", "msf1", "avif", "avis" };

    public static async ValueTask<bool> IsHeifAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(12, cancellationToken);
        if (sniff.Length < 12) return false;

        var span = sniff.Span;
        if (BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0, 4)) < 8) return false;
        if (System.Text.Encoding.ASCII.GetString(span.Slice(4, 4)) != "ftyp") return false;

        string majorBrand = System.Text.Encoding.ASCII.GetString(span.Slice(8, 4));
        foreach (var brand in HeifBrands)
        {
            if (majorBrand.StartsWith(brand)) return true;
        }

        return false;
    }

    public static async ValueTask<VipsImage?> LoadHeaderAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsHeifAsync(source, cancellationToken))
            return null;

        // Manual ISOBMFF Parser for HEIF/AVIF metadata (Fast & works on dummy data)
        var ms = new MemoryStream();
        var buffer = new byte[81920];
        // Note: For header only, we don't consume the source, just sniff or read a bit.
        // But since we can't rewind the pipe, we'll use Sniff to get enough data if possible.
        var sniff = await source.SniffAsync(81920, cancellationToken);
        if (sniff.Length < 16) return null;
        
        ms.Write(sniff.Span);
        ms.Position = 0;

        int width = 0, height = 0;

        while (ms.Position + 8 <= ms.Length)
        {
            var header = new byte[8];
            ms.Read(header, 0, 8);

            uint size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            string type = System.Text.Encoding.ASCII.GetString(header.AsSpan(4, 4));

            if (type == "ftyp")
            {
                ms.Seek(size - 8, SeekOrigin.Current);
            }
            else if (type == "meta")
            {
                ms.Read(new byte[4], 0, 4);
                continue; 
            }
            else if (type == "iprp" || type == "ipco")
            {
                continue;
            }
            else if (type == "ispe")
            {
                var data = new byte[12];
                ms.Read(data, 0, 12);
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
                
                return new VipsImage
                {
                    Width = width,
                    Height = height,
                    Bands = 3,
                    BandFormat = VipsBandFormat.UChar,
                    Interpretation = VipsInterpretation.RGB,
                    XRes = 1.0,
                    YRes = 1.0
                };
            }
            else
            {
                if (size == 1) // 64-bit size
                {
                    var largeSizeBuf = new byte[8];
                    if (ms.Read(largeSizeBuf, 0, 8) < 8) break;
                    long largeSize = (long)BinaryPrimitives.ReadUInt64BigEndian(largeSizeBuf);
                    if (ms.Position + largeSize - 16 > ms.Length) break;
                    ms.Seek(largeSize - 16, SeekOrigin.Current);
                }
                else if (size == 0) // Till end of file
                {
                    break;
                }
                else
                {
                    if (ms.Position + size - 8 > ms.Length) break;
                    ms.Seek(size - 8, SeekOrigin.Current);
                }
            }
        }

        return null;
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsHeifAsync(source, cancellationToken))
            return null;

        var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int readCount = await source.ReadAsync(buffer, cancellationToken);
            if (readCount == 0) break;
            ms.Write(buffer, 0, readCount);
        }

        var imageBytes = ms.ToArray();
        
        // Try to get header from bytes
        var memSource = new PipeVipsSource(System.IO.Pipelines.PipeReader.Create(new MemoryStream(imageBytes)));
        var image = await LoadHeaderAsync(memSource, cancellationToken);
        if (image == null) return null;

        // Eagerly probe Magick.NET for profiles. We do an extra parse here so
        // the metadata is captured at load time even if the caller never
        // touches PixelsLazy. Pixel decode stays lazy in the closure below.
        try
        {
            using var probe = new MagickImage(imageBytes);
            var exifProfile = probe.GetProfile("exif");
            if (exifProfile != null) image.MetadataBlobs["exif"] = exifProfile.ToByteArray();
            var xmpProfile = probe.GetProfile("xmp");
            if (xmpProfile != null) image.MetadataBlobs["xmp"] = xmpProfile.ToByteArray();
            var iccProfile = probe.GetColorProfile();
            if (iccProfile != null) image.MetadataBlobs["icc"] = iccProfile.ToByteArray();
        }
        catch { /* metadata extraction is best-effort; load shouldn't fail on it */ }

        image.PixelsLazy = new Lazy<byte[]>(() =>
        {
            using var magickImage = new MagickImage(imageBytes);
            int width = (int)magickImage.Width;
            int height = (int)magickImage.Height;
            int bands = magickImage.HasAlpha ? 4 : 3;
            int stride = width * bands;
            var buf = new byte[stride * height];

            if (bands == 4) magickImage.ColorSpace = ColorSpace.sRGB;
            else magickImage.Alpha(AlphaOption.Off);

            using var pixels = magickImage.GetPixels();
            for (int y = 0; y < height; y++)
            {
                var row = pixels.GetArea(0, y, (uint)width, 1)
                    ?? throw new InvalidOperationException($"HEIF: pixel row {y} returned null");
                Array.Copy(row, 0, buf, y * stride, stride);
            }
            return buf;
        });

        return image;
    }
}
