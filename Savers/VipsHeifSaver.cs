using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Savers;

/// <summary>
/// HEIC / AVIF encoder stubs. <b>Not implemented.</b>
///
/// <para>HEIF (HEIC) needs an HEVC encoder; AVIF needs an AV1 encoder.
/// Both are multi-year codec projects. The previous Magick.NET-backed
/// implementation was removed when CosmoImage dropped Magick.NET from its
/// production dependency surface (see <c>CONTRIBUTING.md</c>).</para>
///
/// <para>Both methods throw <see cref="NotSupportedException"/> with a
/// clear message pointing at the missing codec — callers can either route
/// to a different format (PNG / WebP for graphics, JPEG for photos) or
/// strip the HEIF dispatch entirely upstream.</para>
/// </summary>
public static class VipsHeifSaver
{
    public static Task SaveHeifAsync(VipsImage image, PipeWriter writer, int quality = 75, bool lossless = false, CancellationToken cancellationToken = default)
    {
        _ = image; _ = writer; _ = quality; _ = lossless; _ = cancellationToken;
        throw new NotSupportedException(
            "HEIF / HEIC encode is not implemented. CosmoImage requires a pure-managed " +
            "HEVC encoder (out of scope for the foreseeable future). Encode to PNG / WebP " +
            "lossless / JPEG instead, or apply HEIC encoding upstream with a different tool.");
    }

    public static Task SaveAvifAsync(VipsImage image, PipeWriter writer, int quality = 75, bool lossless = false, CancellationToken cancellationToken = default)
    {
        _ = image; _ = writer; _ = quality; _ = lossless; _ = cancellationToken;
        throw new NotSupportedException(
            "AVIF encode is not implemented. CosmoImage requires a pure-managed AV1 " +
            "encoder (out of scope for the foreseeable future). Encode to PNG / WebP " +
            "lossless / JPEG instead, or apply AVIF encoding upstream with a different tool.");
    }
}
