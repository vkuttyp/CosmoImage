using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

/// <summary>
/// WebP loader, pure-managed (no native deps). Decodes VP8L (lossless)
/// via <see cref="PureWebpLossless"/>; extracts EXIF / XMP / ICCP metadata
/// via the in-line RIFF walker.
///
/// <para>Scope: <b>VP8L lossless only</b>. VP8 lossy and animated WebPs
/// return <c>null</c> from <see cref="LoadAsync"/> — the loader dispatch
/// layer is expected to skip to the next handler. (Lossy decode requires
/// a full VP8 codec port, which is out of scope for this phase.)</para>
/// </summary>
public static class VipsWebpLoader
{
    public static async ValueTask<bool> IsWebpAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(12, cancellationToken);
        if (sniff.Length < 12) return false;

        var span = sniff.Span;
        return span[0] == (byte)'R' && span[1] == (byte)'I' && span[2] == (byte)'F' && span[3] == (byte)'F' &&
               span[8] == (byte)'W' && span[9] == (byte)'E' && span[10] == (byte)'B' && span[11] == (byte)'P';
    }

    /// <summary>
    /// Parse the VP8X / VP8 / VP8L chunk to expose dimensions without
    /// decoding the pixel data. Pure-managed; no codec invoked.
    /// </summary>
    public static async ValueTask<VipsImage?> LoadHeaderAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsWebpAsync(source, cancellationToken))
            return null;

        var headerBuffer = new byte[12];
        await source.ReadAsync(headerBuffer, cancellationToken);

        var chunkBuffer = new byte[8];
        while (true)
        {
            var read = await source.ReadAsync(chunkBuffer, cancellationToken);
            if (read < 8) break;

            uint type = BinaryPrimitives.ReadUInt32BigEndian(chunkBuffer.AsSpan(0, 4));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(chunkBuffer.AsSpan(4, 4));

            if (type == 0x56503858) // "VP8X"
            {
                var data = new byte[10];
                read = await source.ReadAsync(data, cancellationToken);
                if (read < 10) break;

                bool hasAlpha = (data[0] & 0x10) != 0;
                int width  = 1 + (data[4] | (data[5] << 8) | (data[6] << 16));
                int height = 1 + (data[7] | (data[8] << 8) | (data[9] << 16));

                return new VipsImage
                {
                    Width = width,
                    Height = height,
                    Bands = hasAlpha ? 4 : 3,
                    BandFormat = VipsBandFormat.UChar,
                    Interpretation = VipsInterpretation.RGB,
                    Coding = VipsCoding.None,
                    XRes = 1.0,
                    YRes = 1.0
                };
            }
            else if (type == 0x56503820) // "VP8 " (lossy)
            {
                var data = new byte[10];
                read = await source.ReadAsync(data, cancellationToken);
                if (read < 10) break;

                if ((data[0] & 1) == 0 && data[3] == 0x9D && data[4] == 0x01 && data[5] == 0x2A)
                {
                    int width  = (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6, 2)) & 0x3FFF);
                    int height = (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8, 2)) & 0x3FFF);

                    return new VipsImage
                    {
                        Width = width,
                        Height = height,
                        Bands = 3,
                        BandFormat = VipsBandFormat.UChar,
                        Interpretation = VipsInterpretation.RGB,
                        Coding = VipsCoding.None,
                        XRes = 1.0,
                        YRes = 1.0
                    };
                }
            }
            else if (type == 0x5650384C) // "VP8L"
            {
                var data = new byte[5];
                read = await source.ReadAsync(data, cancellationToken);
                if (read < 5) break;

                if (data[0] == 0x2F)
                {
                    uint val = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1, 4));
                    int width  = (int)((val & 0x3FFF) + 1);
                    int height = (int)(((val >> 14) & 0x3FFF) + 1);
                    bool hasAlpha = ((val >> 28) & 1) != 0;

                    return new VipsImage
                    {
                        Width = width,
                        Height = height,
                        Bands = hasAlpha ? 4 : 3,
                        BandFormat = VipsBandFormat.UChar,
                        Interpretation = VipsInterpretation.RGB,
                        Coding = VipsCoding.None,
                        XRes = 1.0,
                        YRes = 1.0
                    };
                }
            }

            uint toSkip = (length + 1) & ~1u;
            var skipBuffer = new byte[Math.Min(toSkip, 4096)];
            while (toSkip > 0)
            {
                int chunkToRead = (int)Math.Min(toSkip, (uint)skipBuffer.Length);
                read = await source.ReadAsync(skipBuffer.AsMemory(0, chunkToRead), cancellationToken);
                if (read <= 0) break;
                toSkip -= (uint)read;
            }
        }

        return null;
    }

    /// <summary>
    /// Full decode. Returns the VipsImage on VP8L (lossless) success, or
    /// <c>null</c> for VP8 (lossy), animated (ANIM/ANMF), or malformed
    /// streams — the dispatch layer is expected to treat null as "this
    /// loader can't handle the input".
    /// </summary>
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

        return DecodeBytes(ms.ToArray());
    }

    /// <summary>
    /// Streaming variant. Same scope (VP8L only); the streaming guarantee
    /// is moot for VP8L because the entire bitstream is required before
    /// any pixel can be produced — we still buffer to a byte array.
    /// </summary>
    public static async ValueTask<VipsImage?> LoadStreamingAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsWebpAsync(source, cancellationToken)) return null;
        await Task.Yield();

        using var stream = source.AsStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return DecodeBytes(ms.ToArray());
    }

    private static VipsImage? DecodeBytes(byte[] imageBytes)
    {
        var image = PureWebpLossless.TryDecode(imageBytes);
        if (image == null)
        {
            // VP8 (lossy) or animated (ANIM/ANMF) or malformed — out of
            // scope for the pure-managed loader. Returning null lets the
            // dispatch layer fall through.
            return null;
        }

        AttachWebpMetadata(imageBytes, image);
        return image;
    }

    /// <summary>
    /// Pure-managed RIFF chunk walker for EXIF / XMP / ICCP. Stops at the
    /// first VP8/VP8L chunk (metadata always precedes image data when
    /// present in a VP8X-wrapped file). Best-effort: swallows malformed
    /// chunks rather than failing the load.
    /// </summary>
    private static void AttachWebpMetadata(byte[] bytes, VipsImage image)
    {
        if (bytes.Length < 12 + 8) return;
        int p = 12; // skip RIFF<size>WEBP

        while (p + 8 <= bytes.Length)
        {
            uint fourcc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4));
            int len = BitConverter.ToInt32(bytes, p + 4);
            int payload = p + 8;
            if (len < 0 || payload + len > bytes.Length) return;

            switch (fourcc)
            {
                case 0x50434349: // "ICCP"
                    image.MetadataBlobs["icc"] = bytes.AsSpan(payload, len).ToArray();
                    break;
                case 0x46495845: // "EXIF"
                    image.MetadataBlobs["exif"] = bytes.AsSpan(payload, len).ToArray();
                    break;
                case 0x20504D58: // "XMP "
                    image.MetadataBlobs["xmp"] = bytes.AsSpan(payload, len).ToArray();
                    break;
                case 0x4C385056: // "VP8L"
                case 0x20385056: // "VP8 "
                    return;
            }

            p = payload + len + (len & 1); // RIFF pads payloads to even.
        }
    }
}
