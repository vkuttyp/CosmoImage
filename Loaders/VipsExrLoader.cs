using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Core;

namespace CosmoImage.Loaders;

/// <summary>
/// OpenEXR loader. Pure-managed decoder for the foundational subset:
/// single-part scanline files, NO_COMPRESSION, HALF pixel type, and
/// recognised channel sets (R/G/B + optional A, or single-channel Y).
/// Subsequent rounds add compressors (RLE, ZIP, PIZ, PXR24, B44, DWA),
/// tiled layouts, multi-part files, and richer pixel types.
/// </summary>
public static class VipsExrLoader
{
    public static async ValueTask<bool> IsExrAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(4, cancellationToken);
        if (sniff.Length < 4) return false;
        int magic = BinaryPrimitives.ReadInt32LittleEndian(sniff.Span);
        return magic == PureExrDecoder.MagicLE;
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsExrAsync(source, cancellationToken)) return null;
        var ms = new MemoryStream();
        var buf = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buf, cancellationToken);
            if (read == 0) break;
            ms.Write(buf, 0, read);
        }
        var bytes = ms.ToArray();
        return PureExrDecoder.TryDecode(bytes);
    }
}
