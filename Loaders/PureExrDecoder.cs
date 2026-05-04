using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using CosmoImage.Core;

namespace CosmoImage.Loaders;

/// <summary>
/// Pure-managed OpenEXR decoder. Reads the file header (magic +
/// version + attribute list), then decodes scanline-organised
/// uncompressed pixel data into a <see cref="VipsImage"/>.
///
/// <para>This first pass covers the foundational subset:</para>
/// <list type="bullet">
///   <item>Single-part files only (multi-part is bit 12 of the version word)</item>
///   <item>Scanline organisation (tiled handled by a later round)</item>
///   <item>NO_COMPRESSION (=0); RLE / ZIP / PIZ / PXR24 / B44 / DWA come later</item>
///   <item>HALF (=1) pixel type — converted to <see cref="float"/> on decode</item>
///   <item>Channels named "R" / "G" / "B" / "A" — other channel sets fall through</item>
/// </list>
///
/// <para>Returns <c>null</c> for unsupported configurations so the
/// caller can fall back. Output is <see cref="VipsBandFormat.Float"/>
/// because EXR's HALF pixels promote naturally and downstream ops
/// handle Float; HALF-format storage in the <see cref="VipsImage"/>
/// is a future optimisation.</para>
/// </summary>
internal static class PureExrDecoder
{
    public const int MagicLE = 0x01312F76;

    public static VipsImage? TryDecode(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 12) return null;
        int magic = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4));
        if (magic != MagicLE) return null;
        int version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4));
        int formatVersion = version & 0xFF;
        if (formatVersion != 2) return null;

        bool tiled = (version & 0x200) != 0;
        bool longNames = (version & 0x400) != 0;
        bool nonImage = (version & 0x800) != 0;
        bool multiPart = (version & 0x1000) != 0;
        if (tiled || nonImage || multiPart) return null;  // future rounds

        int p = 8;
        var header = new ExrHeader();
        if (!ParseAttributes(bytes, ref p, longNames, header)) return null;
        if (!header.IsValid()) return null;

        // Validate: scanline order, supported compression, supported channel set.
        if (header.Compression != 0) return null;          // NO_COMPRESSION only here
        if (header.LineOrder != 0 && header.LineOrder != 1) return null;

        // Determine output bands based on which channels are present.
        // EXR channels are stored alphabetically inside the file; we
        // canonicalise to RGBA on output.
        int[]? channelOrder = ResolveChannelOrder(header.Channels, out int outBands);
        if (channelOrder == null) return null;
        // All requested channels must use HALF format.
        foreach (int idx in channelOrder)
            if (header.Channels[idx].PixelType != PixelTypeHalf) return null;

        int width = header.DataWindow.Width;
        int height = header.DataWindow.Height;
        if (width <= 0 || height <= 0) return null;

        // After attributes (terminated by a single null byte), the
        // scanline offset table follows: one uint64 per scanline.
        int offsetTableStart = p;
        long need = (long)offsetTableStart + 8L * height;
        if (need > bytes.Length) return null;

        var pixels = new float[(long)width * height * outBands];

        // Number of channels in the FILE's scanline (which includes any
        // unselected channels, alphabetically). Each scanline has all of
        // those channels' bytes laid out per-channel-then-per-pixel.
        int allChannels = header.Channels.Count;
        long fileRowBytes = 0;
        foreach (var c in header.Channels) fileRowBytes += BytesPerChannelSample(c.PixelType) * width;

        for (int y = 0; y < height; y++)
        {
            long off = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offsetTableStart + y * 8, 8));
            if (off < 0 || off + 8 > bytes.Length) return null;
            int yCoord = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)off, 4));
            int dataSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)off + 4, 4));
            int rowDataOff = (int)off + 8;
            if (rowDataOff + dataSize > bytes.Length) return null;
            if (yCoord < header.DataWindow.YMin || yCoord > header.DataWindow.YMax) return null;
            // For LineOrder=0 the file has scanlines in increasing-Y; for
            // LineOrder=1 they're decreasing-Y. Either way, yCoord tells
            // us where this block belongs.
            int outY = yCoord - header.DataWindow.YMin;
            if ((long)dataSize < fileRowBytes) return null;  // uncompressed

            DemuxScanline(bytes, rowDataOff, header.Channels, channelOrder, width, pixels, outY, outBands);
        }

        return new VipsImage
        {
            Width = width,
            Height = height,
            Bands = outBands,
            BandFormat = VipsBandFormat.Float,
            Interpretation = outBands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() =>
            {
                var dst = new byte[pixels.Length * 4];
                Buffer.BlockCopy(pixels, 0, dst, 0, dst.Length);
                return dst;
            }),
        };
    }

    /// <summary>
    /// Demux a single scanline from the file's per-channel layout
    /// (all of channel 0's pixels, then all of channel 1's, etc.) into
    /// the output buffer's chunky-RGBA layout. Skips any file channels
    /// we're not selecting.
    /// </summary>
    private static void DemuxScanline(byte[] bytes, int srcOff,
        List<ExrChannel> fileChannels, int[] channelOrder,
        int width, float[] dstPixels, int outY, int outBands)
    {
        // Build per-channel source offsets within this scanline block.
        int[] channelOffsets = new int[fileChannels.Count];
        int cursor = srcOff;
        for (int i = 0; i < fileChannels.Count; i++)
        {
            channelOffsets[i] = cursor;
            cursor += BytesPerChannelSample(fileChannels[i].PixelType) * width;
        }

        long dstRowBase = (long)outY * width * outBands;
        for (int outCh = 0; outCh < outBands; outCh++)
        {
            int srcChIdx = channelOrder[outCh];
            int co = channelOffsets[srcChIdx];
            // HALF only for now.
            for (int x = 0; x < width; x++)
            {
                ushort half = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(co + x * 2, 2));
                dstPixels[dstRowBase + (long)x * outBands + outCh] = HalfToFloat(half);
            }
        }
    }

    /// <summary>
    /// Decide the output band count + the file-channel index for each
    /// output band. Returns null when no recognised channel set is
    /// found.
    /// </summary>
    private static int[]? ResolveChannelOrder(List<ExrChannel> channels, out int outBands)
    {
        outBands = 0;
        // Channels are stored alphabetically in the file. Look up by name.
        int? r = FindChannel(channels, "R");
        int? g = FindChannel(channels, "G");
        int? b = FindChannel(channels, "B");
        int? a = FindChannel(channels, "A");
        int? y = FindChannel(channels, "Y");
        if (r.HasValue && g.HasValue && b.HasValue)
        {
            if (a.HasValue) { outBands = 4; return new[] { r.Value, g.Value, b.Value, a.Value }; }
            outBands = 3; return new[] { r.Value, g.Value, b.Value };
        }
        if (y.HasValue)
        {
            outBands = 1; return new[] { y.Value };
        }
        return null;
    }

    private static int? FindChannel(List<ExrChannel> channels, string name)
    {
        for (int i = 0; i < channels.Count; i++)
            if (channels[i].Name == name) return i;
        return null;
    }

    private static int BytesPerChannelSample(int pixelType) => pixelType switch
    {
        PixelTypeUint => 4,
        PixelTypeHalf => 2,
        PixelTypeFloat => 4,
        _ => 0,
    };

    public const int PixelTypeUint = 0;
    public const int PixelTypeHalf = 1;
    public const int PixelTypeFloat = 2;

    /// <summary>Decode a 16-bit IEEE 754 half-precision float to <see cref="float"/>.</summary>
    private static float HalfToFloat(ushort h)
    {
        // .NET 5+ has Half. Use BitConverter to convert.
        return (float)BitConverter.UInt16BitsToHalf(h);
    }

    // ---- Header parsing ----

    private sealed class ExrHeader
    {
        public List<ExrChannel> Channels { get; } = new();
        public int Compression { get; set; } = -1;
        public Box2i DataWindow { get; set; }
        public Box2i DisplayWindow { get; set; }
        public int LineOrder { get; set; } = -1;
        public float PixelAspectRatio { get; set; } = 1.0f;
        public bool IsValid() => Channels.Count > 0 && Compression >= 0
                                  && DataWindow.Width > 0 && DataWindow.Height > 0
                                  && LineOrder >= 0;
    }

    private sealed class ExrChannel
    {
        public string Name { get; init; } = "";
        public int PixelType { get; init; }
        public byte PLinear { get; init; }
        public int XSampling { get; init; } = 1;
        public int YSampling { get; init; } = 1;
    }

    private struct Box2i
    {
        public int XMin, YMin, XMax, YMax;
        public int Width => XMax - XMin + 1;
        public int Height => YMax - YMin + 1;
    }

    /// <summary>
    /// Parse the attribute list. Each attribute is name + type + size +
    /// payload. The list ends with a single null byte.
    /// </summary>
    private static bool ParseAttributes(byte[] bytes, ref int p, bool longNames, ExrHeader hdr)
    {
        int maxNameLen = longNames ? 255 : 31;
        while (p < bytes.Length)
        {
            if (bytes[p] == 0) { p++; return true; }  // end of attributes

            string? name = ReadNullTerminated(bytes, ref p, maxNameLen);
            if (name == null) return false;
            if (p >= bytes.Length) return false;

            string? type = ReadNullTerminated(bytes, ref p, maxNameLen);
            if (type == null) return false;
            if (p + 4 > bytes.Length) return false;

            int size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p, 4));
            p += 4;
            if (size < 0 || p + size > bytes.Length) return false;
            int valueStart = p;
            int valueEnd = p + size;
            p = valueEnd;

            switch (name)
            {
                case "channels":
                    if (!ParseChannels(bytes, valueStart, valueEnd, hdr.Channels)) return false;
                    break;
                case "compression":
                    if (size < 1) return false;
                    hdr.Compression = bytes[valueStart];
                    break;
                case "dataWindow":
                    if (size < 16) return false;
                    hdr.DataWindow = ReadBox2i(bytes, valueStart);
                    break;
                case "displayWindow":
                    if (size < 16) return false;
                    hdr.DisplayWindow = ReadBox2i(bytes, valueStart);
                    break;
                case "lineOrder":
                    if (size < 1) return false;
                    hdr.LineOrder = bytes[valueStart];
                    break;
                case "pixelAspectRatio":
                    if (size >= 4)
                        hdr.PixelAspectRatio = BitConverter.Int32BitsToSingle(
                            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(valueStart, 4)));
                    break;
                // Other attributes (screenWindowCenter, screenWindowWidth, etc.)
                // are accepted-and-skipped.
            }
        }
        return false;  // hit EOF without seeing the terminating null
    }

    private static bool ParseChannels(byte[] bytes, int start, int end, List<ExrChannel> channels)
    {
        int p = start;
        while (p < end)
        {
            if (bytes[p] == 0) { p++; return true; }
            string? name = ReadNullTerminated(bytes, ref p, 255);
            if (name == null) return false;
            if (p + 16 > end) return false;
            int pixelType = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p, 4));
            byte pLinear = bytes[p + 4];
            int xSampling = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 8, 4));
            int ySampling = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 12, 4));
            p += 16;
            channels.Add(new ExrChannel
            {
                Name = name, PixelType = pixelType, PLinear = pLinear,
                XSampling = xSampling, YSampling = ySampling,
            });
        }
        return false;
    }

    private static Box2i ReadBox2i(byte[] bytes, int p) => new()
    {
        XMin = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p, 4)),
        YMin = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 4, 4)),
        XMax = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 8, 4)),
        YMax = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 12, 4)),
    };

    private static string? ReadNullTerminated(byte[] bytes, ref int p, int maxLen)
    {
        int start = p;
        while (p < bytes.Length && p - start < maxLen && bytes[p] != 0) p++;
        if (p >= bytes.Length || bytes[p] != 0) return null;
        string s = Encoding.ASCII.GetString(bytes, start, p - start);
        p++;  // skip null
        return s;
    }
}
