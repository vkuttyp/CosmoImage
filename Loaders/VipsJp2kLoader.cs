using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

public static class VipsJp2kLoader
{
    private static readonly byte[] Jp2Magic = { 0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A };
    private static readonly byte[] J2kMagic = { 0xFF, 0x4F, 0xFF, 0x51 };

    public static async ValueTask<bool> IsJp2kAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(12, cancellationToken);
        if (sniff.Length < 4) return false;

        var span = sniff.Span;
        if (span.StartsWith(Jp2Magic)) return true;
        if (span.StartsWith(J2kMagic)) return true;
        
        return false;
    }

    public static async ValueTask<VipsImage?> LoadHeaderAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(12, cancellationToken);
        if (sniff.Length < 4) return null;

        if (sniff.Span.StartsWith(Jp2Magic))
        {
            return await LoadJp2HeaderAsync(source, cancellationToken);
        }
        else if (sniff.Span.StartsWith(J2kMagic))
        {
            return await LoadJ2kHeaderAsync(source, cancellationToken);
        }

        return null;
    }

    private static async ValueTask<VipsImage?> LoadJp2HeaderAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        // Skip signature (12 bytes)
        var buffer = new byte[12];
        await source.ReadAsync(buffer, cancellationToken);

        while (true)
        {
            var boxHeader = new byte[8];
            int read = await source.ReadAsync(boxHeader, cancellationToken);
            if (read < 8) break;

            uint length = BinaryPrimitives.ReadUInt32BigEndian(boxHeader.AsSpan(0, 4));
            uint type = BinaryPrimitives.ReadUInt32BigEndian(boxHeader.AsSpan(4, 4));

            if (type == 0x6A703268) // "jp2h"
            {
                // We are inside jp2h, next should be boxes
                // Length is the whole box, we need to read its content
                // But ihdr is usually the first box inside jp2h
                continue; 
            }

            if (type == 0x69686472) // "ihdr"
            {
                var ihdrData = new byte[14];
                read = await source.ReadAsync(ihdrData, cancellationToken);
                if (read < 14) break;

                int height = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdrData.AsSpan(0, 4));
                int width = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdrData.AsSpan(4, 4));
                ushort nc = BinaryPrimitives.ReadUInt16BigEndian(ihdrData.AsSpan(8, 2));
                byte bpc = ihdrData[10];

                return new VipsImage
                {
                    Width = width,
                    Height = height,
                    Bands = nc,
                    BandFormat = (bpc & 0x7F) > 7 ? VipsBandFormat.UShort : VipsBandFormat.UChar,
                    Interpretation = nc <= 2 ? VipsInterpretation.BW : VipsInterpretation.RGB,
                    XRes = 1.0,
                    YRes = 1.0
                };
            }

            // Skip box
            long toSkip = length - 8;
            if (length == 1) // Extended length
            {
                var extLength = new byte[8];
                await source.ReadAsync(extLength, cancellationToken);
                toSkip = (long)BinaryPrimitives.ReadUInt64BigEndian(extLength) - 16;
            }

            var skipBuffer = new byte[4096];
            while (toSkip > 0)
            {
                int chunkToRead = (int)Math.Min(toSkip, skipBuffer.Length);
                read = await source.ReadAsync(skipBuffer.AsMemory(0, chunkToRead), cancellationToken);
                if (read <= 0) break;
                toSkip -= read;
            }
        }

        return null;
    }

    private static async ValueTask<VipsImage?> LoadJ2kHeaderAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        // Skip SOC (FF 4F)
        var soc = new byte[2];
        await source.ReadAsync(soc, cancellationToken);

        var buffer = new byte[4];
        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, 2), cancellationToken);
            if (read < 2) break;

            if (buffer[0] != 0xFF) break;
            byte marker = buffer[1];

            if (marker == 0x51) // SIZ
            {
                read = await source.ReadAsync(buffer.AsMemory(2, 2), cancellationToken);
                if (read < 2) break;

                int length = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2, 2));
                var sizData = new byte[length - 2];
                read = await source.ReadAsync(sizData, cancellationToken);
                if (read < sizData.Length) break;

                // SIZ data starts at index 0 (after marker and length)
                // Capabilities (2), Width (4), Height (4), Offset X (4), Offset Y (4)
                // Tile Width (4), Tile Height (4), Tile Offset X (4), Tile Offset Y (4)
                // Number of components (2)
                
                int width = (int)BinaryPrimitives.ReadUInt32BigEndian(sizData.AsSpan(2, 4));
                int height = (int)BinaryPrimitives.ReadUInt32BigEndian(sizData.AsSpan(6, 4));
                int nc = BinaryPrimitives.ReadUInt16BigEndian(sizData.AsSpan(34, 2));

                return new VipsImage
                {
                    Width = width,
                    Height = height,
                    Bands = nc,
                    BandFormat = VipsBandFormat.UChar, // Default to UChar for now
                    Interpretation = nc <= 2 ? VipsInterpretation.BW : VipsInterpretation.RGB,
                    XRes = 1.0,
                    YRes = 1.0
                };
            }
            else
            {
                // Skip segment
                read = await source.ReadAsync(buffer.AsMemory(2, 2), cancellationToken);
                if (read < 2) break;
                int length = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2, 2)) - 2;
                
                var skipBuffer = new byte[4096];
                while (length > 0)
                {
                    int toRead = Math.Min(length, skipBuffer.Length);
                    read = await source.ReadAsync(skipBuffer.AsMemory(0, toRead), cancellationToken);
                    if (read <= 0) break;
                    length -= read;
                }
            }
        }

        return null;
    }
}
