using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using JpegLibrary;

namespace CosmoImage.Savers;

public static class VipsJpegSaver
{
    // The JPEG encoder is a pull model: Encode() calls ReadBlock(component, x, y)
    // for arbitrary 8x8 MCU blocks, visiting each block once per component (so
    // each block is re-fetched up to 3 times for color). Calling Prepare per
    // block is wasteful. Instead we materialize the entire image into a flat
    // raw buffer up-front via VipsSink (parallel pixel preparation) and have
    // ReadBlock read from that buffer.
    private class VipsInputReader : JpegBlockInputReader
    {
        private readonly int _width;
        private readonly int _height;
        private readonly int _bands;
        private readonly int _pelSize;
        private readonly byte[] _pixels;

        public VipsInputReader(int width, int height, int bands, int pelSize, byte[] pixels)
        {
            _width = width;
            _height = height;
            _bands = bands;
            _pelSize = pelSize;
            _pixels = pixels;
        }

        public override int Width => _width;
        public override int Height => _height;

        public override void ReadBlock(ref short blockRef, int componentIndex, int x, int y)
        {
            int stride = _width * _pelSize;

            for (int row = 0; row < 8; row++)
            {
                int fetchY = Math.Min(y + row, _height - 1);
                int rowBase = fetchY * stride;

                for (int col = 0; col < 8; col++)
                {
                    int fetchX = Math.Min(x + col, _width - 1);
                    int offset = rowBase + fetchX * _pelSize;

                    double val;
                    if (_bands >= 3)
                    {
                        byte r = _pixels[offset + 0];
                        byte g = _pixels[offset + 1];
                        byte b = _pixels[offset + 2];

                        val = componentIndex switch
                        {
                            0 => 0.299 * r + 0.587 * g + 0.114 * b, // Y
                            1 => 128 - 0.168736 * r - 0.331264 * g + 0.5 * b, // Cb
                            2 => 128 + 0.5 * r - 0.418688 * g - 0.081312 * b, // Cr
                            _ => 0
                        };
                    }
                    else
                    {
                        val = _pixels[offset]; // Grayscale
                    }

                    // JpegLibrary's encoder expects unshifted unsigned
                    // samples; the level-shift is applied internally
                    // before DCT (symmetric with the decoder side).
                    System.Runtime.CompilerServices.Unsafe.Add(ref blockRef, row * 8 + col) = (short)val;
                }
            }
        }
    }

    public static async Task SaveAsync(VipsImage image, PipeWriter writer, int quality = 75, CancellationToken cancellationToken = default)
    {
        int width = image.Width;
        int height = image.Height;
        int bands = image.Bands;
        int pelSize = image.SizeOfPel;

        // Materialize the source pixels in parallel. MemorySink picks tile shape
        // from the image's DemandHint so SmallTile pipelines (e.g. resize chains)
        // get 128×128 tiles instead of being forced into full-width strips.
        // If the image is already memory-backed (loaded directly from a loader,
        // no transforms in between), skip the materialization entirely.
        byte[] pixels;
        if (image.Pixels is { } existing)
        {
            pixels = existing;
        }
        else
        {
            var sink = new MemorySink(image);
            await sink.RunAsync(cancellationToken);
            pixels = sink.Pixels;
        }

        // Pure-C# encoder. Phase 2 of the JpegLibrary drop. Handles
        // 1-band greyscale and 3-band RGB (encoded as YCbCr 4:2:0) —
        // the dominant on-the-web variants. RGBA (4-band) still routes
        // to JpegLibrary for now since the pure encoder doesn't strip
        // alpha; subsequent rounds will handle that.
        byte[] jpegBytes;
        if (bands == 1 || bands == 3)
        {
            jpegBytes = PureJpegEncoder.Encode(pixels, width, height, bands, quality);
        }
        else
        {
            // Fallback: 4-band or other unusual cases stay on JpegLibrary
            // until the pure encoder grows alpha-strip / CMYK support.
            var encoded = new ArrayBufferWriter<byte>();
            var encoder = new JpegEncoder();
            encoder.SetInputReader(new VipsInputReader(width, height, bands, pelSize, pixels));
            encoder.SetOutput(encoded);

            var lumTable = JpegStandardQuantizationTable.ScaleByQuality(JpegStandardQuantizationTable.GetLuminanceTable(JpegElementPrecision.Precision8Bit, 0), quality);
            var chrTable = JpegStandardQuantizationTable.ScaleByQuality(JpegStandardQuantizationTable.GetChrominanceTable(JpegElementPrecision.Precision8Bit, 1), quality);
            encoder.SetQuantizationTable(lumTable);
            encoder.SetQuantizationTable(chrTable);
            encoder.SetHuffmanTable(true, 0, JpegStandardHuffmanEncodingTable.GetLuminanceDCTable());
            encoder.SetHuffmanTable(false, 0, JpegStandardHuffmanEncodingTable.GetLuminanceACTable());
            encoder.SetHuffmanTable(true, 1, JpegStandardHuffmanEncodingTable.GetChrominanceDCTable());
            encoder.SetHuffmanTable(false, 1, JpegStandardHuffmanEncodingTable.GetChrominanceACTable());
            encoder.AddComponent(1, 0, 0, 0, 1, 1);
            encoder.AddComponent(2, 1, 1, 1, 1, 1);
            encoder.AddComponent(3, 1, 1, 1, 1, 1);
            encoder.Encode();
            jpegBytes = encoded.WrittenMemory.ToArray();
        }

        // Splice metadata: SOI → EXIF (APP1) → XMP (APP1) → rest of encoded JPEG.
        // The encoded stream starts with SOI and may include APP0/JFIF; injecting
        // our APP1 markers right after SOI keeps them in valid position.
        var jpegMemory = jpegBytes.AsMemory();
        if (jpegMemory.Length < 2)
            throw new InvalidOperationException("JPEG encoder produced no output");

        var stream = writer.AsStream();
        await stream.WriteAsync(jpegMemory.Slice(0, 2), cancellationToken); // SOI

        if (image.MetadataBlobs.TryGetValue("exif", out var exifBlob))
            await WriteApp1MarkerAsync(stream, VipsJpegLoader.ExifIdentifier, exifBlob, cancellationToken);
        if (image.MetadataBlobs.TryGetValue("xmp", out var xmpBlob))
            await WriteApp1MarkerAsync(stream, VipsJpegLoader.XmpIdentifier, xmpBlob, cancellationToken);
        if (image.MetadataBlobs.TryGetValue("icc", out var iccBlob))
            await WriteIccApp2MarkersAsync(stream, iccBlob, cancellationToken);

        await stream.WriteAsync(jpegMemory.Slice(2), cancellationToken);

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    private static async Task WriteApp1MarkerAsync(System.IO.Stream stream, byte[] identifier, byte[] payload, CancellationToken ct)
    {
        // APP1 segment length includes the 2 length bytes themselves, the
        // identifier (with its trailing NUL already in the array), and payload.
        // Hard cap at 65533 (length field is uint16, minus the 2 bytes for length).
        int totalLen = 2 + identifier.Length + payload.Length;
        if (totalLen > 65535)
            throw new NotSupportedException(
                $"APP1 segment too large to write in a single marker ({totalLen} > 65535). " +
                "Multi-segment write is not yet implemented.");

        var header = new byte[4];
        header[0] = 0xFF;
        header[1] = 0xE1;
        header[2] = (byte)(totalLen >> 8);
        header[3] = (byte)(totalLen & 0xFF);

        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(identifier, ct);
        await stream.WriteAsync(payload, ct);
    }

    /// <summary>
    /// Write an ICC profile as a sequence of APP2 markers per ICC.1:1998-09
    /// Annex B. Each marker carries "ICC_PROFILE\0" + 1-byte 1-based seq +
    /// 1-byte total + chunk data. Max payload per marker is 65519 bytes.
    /// </summary>
    private static async Task WriteIccApp2MarkersAsync(System.IO.Stream stream, byte[] icc, CancellationToken ct)
    {
        const int chunkSize = 65535 - 2 - 12 - 2; // = 65519
        int totalChunks = (icc.Length + chunkSize - 1) / chunkSize;
        if (totalChunks > 255)
            throw new NotSupportedException($"ICC profile too large ({icc.Length} bytes); APP2 spec allows at most 255 chunks.");

        for (int seq = 1; seq <= totalChunks; seq++)
        {
            int offset = (seq - 1) * chunkSize;
            int chunkLen = Math.Min(chunkSize, icc.Length - offset);
            int markerLen = 2 + VipsJpegLoader.IccIdentifier.Length + 2 + chunkLen;

            var header = new byte[4];
            header[0] = 0xFF;
            header[1] = 0xE2;
            header[2] = (byte)(markerLen >> 8);
            header[3] = (byte)(markerLen & 0xFF);
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(VipsJpegLoader.IccIdentifier, ct);
            await stream.WriteAsync(new byte[] { (byte)seq, (byte)totalChunks }, ct);
            await stream.WriteAsync(icc.AsMemory(offset, chunkLen), ct);
        }
    }
}
