using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Loaders;

namespace CosmoImage.Core;

/// <summary>
/// Custom image-format provider. Implementations expose a name + a
/// magic-byte sniffer + a loader; <see cref="VipsConfiguration"/>
/// consults registered providers BEFORE the built-in dispatch in
/// <see cref="VipsIdentify.LoadAsync(Stream, CancellationToken)"/>,
/// allowing users to ship their own decoders for proprietary or
/// niche formats.
///
/// <para>Sniffers must use peek-style source access (<c>SniffAsync</c>)
/// and not consume bytes — every sniffer sees the source from byte 0.</para>
/// </summary>
public interface IVipsImageFormat
{
    /// <summary>
    /// Human-readable format name (e.g., "FOO", "MyRawCamera"). Used
    /// for save-by-name dispatch via
    /// <see cref="VipsConfiguration.SaveAsync(VipsImage, Stream, string, CancellationToken)"/>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Peek at the source and report whether this provider claims it.
    /// Must not consume bytes — use <c>IVipsSource.SniffAsync</c>.
    /// </summary>
    ValueTask<bool> CanDecodeAsync(IVipsSource source, CancellationToken cancellationToken = default);

    /// <summary>Load the image from the source after a successful sniff.</summary>
    ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this provider implements <see cref="SaveAsync"/>.
    /// Decoder-only providers leave this <c>false</c>; encoders that
    /// override <see cref="SaveAsync"/> should also override this to
    /// return <c>true</c>.
    /// </summary>
    bool CanEncode => false;

    /// <summary>
    /// Encode <paramref name="image"/> to <paramref name="stream"/>.
    /// Default implementation throws — providers that don't implement
    /// encoding leave this alone and report <see cref="CanEncode"/>
    /// = <c>false</c>.
    /// </summary>
    ValueTask SaveAsync(VipsImage image, Stream stream, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"Format '{Name}' does not support encoding");
}

/// <summary>
/// Global configuration registry. Mirrors ImageSharp's
/// <c>Configuration.Default</c> + <c>SetEncoder</c> / <c>SetDecoder</c>
/// pattern, scoped to format dispatch.
///
/// <para>The built-in loaders (PNG, JPEG, WebP, GIF, TIFF, BMP, QOI,
/// HEIF, AVIF, JXL, JP2K, PDF, SVG, HDR, PNM, TGA, FITS, NIfTI, Matlab,
/// CSV) remain hardcoded in <see cref="VipsIdentify"/>. This registry
/// adds a hook BEFORE that dispatch where users can plug in custom
/// providers — useful for proprietary RAW formats, internal binary
/// containers, or replacing a built-in with a more specialised
/// decoder. Custom providers always win sniff conflicts since they're
/// consulted first.</para>
/// </summary>
public sealed class VipsConfiguration
{
    private readonly List<IVipsImageFormat> _formats = new();

    /// <summary>The process-wide default configuration used by every implicit dispatch.</summary>
    public static VipsConfiguration Default { get; } = CreateDefault();

    private static VipsConfiguration CreateDefault()
    {
        var c = new VipsConfiguration();
        c.SeedBuiltIns();
        return c;
    }

    /// <summary>
    /// Drop every registered format and re-seed the built-ins
    /// (PNG / JPEG / WebP / GIF / TIFF / BMP / QOI / HEIF / JXL /
    /// JP2K / PDF / SVG / HDR / FITS / NIfTI / MAT / PNM / TGA).
    /// Useful for tests that want a known starting state.
    /// </summary>
    public void Reset()
    {
        _formats.Clear();
        SeedBuiltIns();
    }

    private void SeedBuiltIns()
    {
        // Built-ins are registered in REVERSE priority order — the
        // reverse-walk in FindMatchAsync hits the most distinctive
        // magic first (PNG, JPEG, ...) and the magic-less fallbacks
        // (PNM, TGA) last.
        foreach (var fmt in CosmoImage.Loaders.BuiltInImageFormats.All())
            _formats.Add(fmt);
    }

    /// <summary>Register a custom format provider. Newer registrations take precedence.</summary>
    public void Register(IVipsImageFormat format)
    {
        if (format == null) throw new ArgumentNullException(nameof(format));
        _formats.Add(format);
    }

    /// <summary>Unregister a previously-registered provider. Returns whether it was present.</summary>
    public bool Unregister(IVipsImageFormat format) => _formats.Remove(format);

    /// <summary>All registered custom format providers (newest last).</summary>
    public IReadOnlyList<IVipsImageFormat> Formats => _formats;

    /// <summary>Drop every registered custom provider. Reverts to built-in-only dispatch.</summary>
    public void Clear() => _formats.Clear();

    /// <summary>
    /// Walk registered providers in reverse registration order
    /// (newest first); return the first whose sniffer claims the
    /// source. <c>null</c> means no provider matched. Snapshots the
    /// formats list before iterating so concurrent mutation
    /// (e.g., test cleanup running in parallel) doesn't trip the loop.
    /// </summary>
    internal async ValueTask<IVipsImageFormat?> FindMatchAsync(IVipsSource source, CancellationToken ct)
    {
        var snapshot = _formats.ToArray();
        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            if (await snapshot[i].CanDecodeAsync(source, ct))
                return snapshot[i];
        }
        return null;
    }

    /// <summary>
    /// Find a registered format by name (case-insensitive). When
    /// multiple registrations share a name, the most recently
    /// registered wins.
    /// </summary>
    public IVipsImageFormat? FindByName(string name)
    {
        if (name == null) return null;
        var snapshot = _formats.ToArray();
        for (int i = snapshot.Length - 1; i >= 0; i--)
            if (string.Equals(snapshot[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return snapshot[i];
        return null;
    }

    /// <summary>
    /// Encode <paramref name="image"/> to <paramref name="stream"/>
    /// via the registered format named <paramref name="formatName"/>
    /// (case-insensitive). Throws <see cref="NotSupportedException"/>
    /// when no registered format with that name supports encoding.
    /// </summary>
    public async ValueTask SaveAsync(VipsImage image, Stream stream, string formatName,
        CancellationToken cancellationToken = default)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (formatName == null) throw new ArgumentNullException(nameof(formatName));
        var fmt = FindByName(formatName);
        if (fmt == null)
            throw new NotSupportedException($"No registered format named '{formatName}'");
        if (!fmt.CanEncode)
            throw new NotSupportedException($"Format '{formatName}' does not support encoding");
        await fmt.SaveAsync(image, stream, cancellationToken);
    }
}
