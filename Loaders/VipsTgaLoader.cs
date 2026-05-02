using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

/// <summary>
/// TGA (Truevision TARGA) loader. Permissive footer-or-magic detection isn't
/// reliable for TGA — the format has no fixed magic at offset 0. We sniff a
/// minimal-validity header (image type byte ∈ known set, color-map type ∈
/// {0,1}) and delegate the decode to Magick.NET, which handles every TGA
/// flavour (RLE, paletted, 16/24/32-bit).
/// </summary>
public static class VipsTgaLoader
{
    public static async ValueTask<bool> IsTgaAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        // TGA header is 18 bytes; image type is at byte 2.
        // Image types: 0 (no image), 1 (uncompressed colour-mapped),
        // 2 (uncompressed RGB), 3 (uncompressed grey), 9/10/11 RLE variants.
        var sniff = await source.SniffAsync(18, cancellationToken);
        if (sniff.Length < 18) return false;
        var s = sniff.Span;
        if (s[1] > 1) return false; // colour-map type
        byte imageType = s[2];
        if (imageType != 0 && imageType != 1 && imageType != 2 && imageType != 3
            && imageType != 9 && imageType != 10 && imageType != 11) return false;
        // pixel depth byte 16 must be one of these
        byte depth = s[16];
        return depth == 8 || depth == 15 || depth == 16 || depth == 24 || depth == 32;
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsTgaAsync(source, cancellationToken)) return null;
        return await VipsMagickWrapLoader.LoadAsync(source, cancellationToken, ImageMagick.MagickFormat.Tga);
    }
}
