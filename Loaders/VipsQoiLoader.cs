using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

/// <summary>
/// QOI (Quite OK Image) loader. Magic bytes are "qoif" at offset 0.
/// Decode delegated to Magick.NET.
/// </summary>
public static class VipsQoiLoader
{
    public static async ValueTask<bool> IsQoiAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(4, cancellationToken);
        if (sniff.Length < 4) return false;
        var s = sniff.Span;
        return s[0] == (byte)'q' && s[1] == (byte)'o' && s[2] == (byte)'i' && s[3] == (byte)'f';
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsQoiAsync(source, cancellationToken)) return null;
        return await VipsMagickWrapLoader.LoadAsync(source, cancellationToken);
    }
}
