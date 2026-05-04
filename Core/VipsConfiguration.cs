using System;
using System.Collections.Generic;
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
    /// <summary>Human-readable format name (e.g., "FOO", "MyRawCamera"). Used for diagnostics.</summary>
    string Name { get; }

    /// <summary>
    /// Peek at the source and report whether this provider claims it.
    /// Must not consume bytes — use <c>IVipsSource.SniffAsync</c>.
    /// </summary>
    ValueTask<bool> CanDecodeAsync(IVipsSource source, CancellationToken cancellationToken = default);

    /// <summary>Load the image from the source after a successful sniff.</summary>
    ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default);
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
    public static VipsConfiguration Default { get; } = new();

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
    /// Walk registered providers in registration order; return the
    /// first whose sniffer claims the source. <c>null</c> means no
    /// custom provider matched (caller should fall through to the
    /// built-in dispatch).
    /// </summary>
    internal async ValueTask<IVipsImageFormat?> FindMatchAsync(IVipsSource source, CancellationToken ct)
    {
        // Walk in reverse so newer registrations win sniff conflicts.
        for (int i = _formats.Count - 1; i >= 0; i--)
        {
            if (await _formats[i].CanDecodeAsync(source, ct))
                return _formats[i];
        }
        return null;
    }
}
