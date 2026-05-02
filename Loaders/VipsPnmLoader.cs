using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

/// <summary>
/// Netpbm family loader: PBM (P1/P4 — bitmap), PGM (P2/P5 — grayscale),
/// PPM (P3/P6 — RGB), PAM (P7 — arbitrary-band). All share the leading
/// <c>P{n}</c> magic. Delegates the decode to Magick.NET.
/// </summary>
public static class VipsPnmLoader
{
    public static async ValueTask<bool> IsPnmAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(2, cancellationToken);
        if (sniff.Length < 2) return false;
        var s = sniff.Span;
        if (s[0] != (byte)'P') return false;
        // Accept P1..P7. P7 is PAM.
        return s[1] >= (byte)'1' && s[1] <= (byte)'7';
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsPnmAsync(source, cancellationToken)) return null;
        return await VipsMagickWrapLoader.LoadAsync(source, cancellationToken);
    }
}
