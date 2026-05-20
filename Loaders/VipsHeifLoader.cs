using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

/// <summary>
/// HEIF / HEIC / AVIF detector + ISOBMFF box-parser for dimensions only.
///
/// <para><b>HEIF decode is not implemented.</b> A pure-managed decoder would
/// require porting HEVC (for HEIC) and AV1 (for AVIF) — both are
/// multi-year codec projects. The previous Magick.NET-backed implementation
/// was removed when CosmoImage dropped Magick.NET from its production
/// dependency surface (see <c>CONTRIBUTING.md</c>).</para>
///
/// <para>What still works:
/// <list type="bullet">
///   <item><see cref="IsHeifAsync"/> — recognises the format from the ftyp
///   brand. Useful for routing / diagnostics.</item>
///   <item><see cref="LoadHeaderAsync"/> — extracts canvas width/height
///   from the <c>ispe</c> box (pure ISOBMFF parsing). Returns a
///   <see cref="VipsImage"/> with dimensions set but no pixel data.</item>
/// </list>
/// <see cref="LoadAsync"/> and <see cref="LoadStreamingAsync"/> return
/// <c>null</c>. The dispatch layer in <c>BuiltInImageFormats</c> treats
/// null as "this loader cannot handle the input" and continues to the
/// next handler.</para>
/// </summary>
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

    /// <summary>
    /// Parse the ISOBMFF box tree for the <c>ispe</c> (image spatial
    /// extents) box to surface canvas dimensions. Pure-managed, no codec
    /// involved. Returns a <see cref="VipsImage"/> with width/height set
    /// but no pixel data attached.
    /// </summary>
    public static async ValueTask<VipsImage?> LoadHeaderAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsHeifAsync(source, cancellationToken))
            return null;

        var sniff = await source.SniffAsync(81920, cancellationToken);
        if (sniff.Length < 16) return null;

        using var ms = new MemoryStream();
        ms.Write(sniff.Span);
        ms.Position = 0;

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
                int width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
                int height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
                return new VipsImage
                {
                    Width = width,
                    Height = height,
                    Bands = 3,
                    BandFormat = VipsBandFormat.UChar,
                    Interpretation = VipsInterpretation.RGB,
                    XRes = 1.0,
                    YRes = 1.0,
                };
            }
            else
            {
                if (size == 1)
                {
                    var largeSizeBuf = new byte[8];
                    if (ms.Read(largeSizeBuf, 0, 8) < 8) break;
                    long largeSize = (long)BinaryPrimitives.ReadUInt64BigEndian(largeSizeBuf);
                    if (ms.Position + largeSize - 16 > ms.Length) break;
                    ms.Seek(largeSize - 16, SeekOrigin.Current);
                }
                else if (size == 0) break;
                else
                {
                    if (ms.Position + size - 8 > ms.Length) break;
                    ms.Seek(size - 8, SeekOrigin.Current);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Always returns <c>null</c>. The pure-managed HEVC/AV1 decoder is
    /// not implemented — see the class-level remarks. Returning null lets
    /// the loader dispatch fall through to the next handler.
    /// </summary>
    public static ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        _ = source;
        _ = cancellationToken;
        return ValueTask.FromResult<VipsImage?>(null);
    }

    /// <summary>Same null contract as <see cref="LoadAsync"/>.</summary>
    public static ValueTask<VipsImage?> LoadStreamingAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        _ = source;
        _ = cancellationToken;
        return ValueTask.FromResult<VipsImage?>(null);
    }
}
