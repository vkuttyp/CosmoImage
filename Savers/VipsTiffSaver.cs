using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Savers;

/// <summary>
/// Native classic-TIFF writer. Handles single-page and multi-page output using
/// the same n-pages/page-height metadata convention as the animated savers.
/// Writes little-endian baseline TIFF with Deflate-compressed strips or tiles.
/// </summary>
public static class VipsTiffSaver
{
    public static async Task SaveAsync(VipsImage image, PipeWriter writer,
        bool pyramid = false, int tileSize = 0,
        CancellationToken cancellationToken = default)
    {
        int width = image.Width;
        int height = image.Height;
        int bands = image.Bands;

        if (bands != 1 && bands != 3 && bands != 4)
            throw new NotSupportedException($"TIFF save needs 1, 3, or 4 bands; got {bands}");
        if (tileSize < 0)
            throw new ArgumentOutOfRangeException(nameof(tileSize));

        int nPages = 1;
        int pageHeight = height;
        if (image.Metadata.TryGetValue("n-pages", out var npStr) &&
            int.TryParse(npStr, out int parsedPages) && parsedPages > 0 &&
            image.Metadata.TryGetValue("page-height", out var phStr) &&
            int.TryParse(phStr, out int parsedPageHeight) && parsedPageHeight > 0 &&
            parsedPages * parsedPageHeight == height)
        {
            nPages = parsedPages;
            pageHeight = parsedPageHeight;
        }

        bool sixteenBit = image.BandFormat == VipsBandFormat.UShort;
        int bytesPerSample = sixteenBit ? 2 : 1;
        VipsImage src = image.BandFormat == VipsBandFormat.UChar || sixteenBit
            ? image
            : VipsImageOps.CastUChar(image);

        byte[] pixels;
        if (src.Pixels is { } existing)
        {
            pixels = existing;
        }
        else
        {
            var sink = new MemorySink(src);
            await sink.RunAsync(cancellationToken);
            pixels = sink.Pixels;
        }

        // Pyramidal TIFF previously relied on Magick's Ptif encoder. Until a
        // native pyramid writer exists, keep the knob as a no-op standard TIFF.
        _ = pyramid;

        string? description = null;
        if (image.Metadata.TryGetValue("ome:xml", out var ome) && !string.IsNullOrEmpty(ome))
            description = ome;
        else if (image.Metadata.TryGetValue("tiff:image-description", out var generic) && !string.IsNullOrEmpty(generic))
            description = generic;

        int? orientation = null;
        if (image.Metadata.TryGetValue("orientation", out var orientText) &&
            int.TryParse(orientText, out int orientValue) &&
            orientValue >= 1 && orientValue <= 8)
            orientation = orientValue;

        var encoded = EncodeClassicTiff(
            pixels,
            width,
            pageHeight,
            nPages,
            bands,
            bytesPerSample,
            tileSize,
            description,
            orientation,
            image.MetadataBlobs.TryGetValue("icc", out var icc) ? icc : null,
            image.MetadataBlobs.TryGetValue("xmp", out var xmp) ? xmp : null);

        await writer.WriteAsync(encoded, cancellationToken);
        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    private static byte[] EncodeClassicTiff(
        byte[] pixels,
        int width,
        int pageHeight,
        int nPages,
        int bands,
        int bytesPerSample,
        int tileSize,
        string? description,
        int? orientation,
        byte[]? icc,
        byte[]? xmp)
    {
        var pages = BuildPages(pixels, width, pageHeight, nPages, bands, bytesPerSample, tileSize);

        using var ms = new MemoryStream();
        // Header: little-endian classic TIFF, first IFD immediately after header.
        ms.WriteByte(0x49);
        ms.WriteByte(0x49);
        WriteU16(ms, 0x002A);
        WriteU32(ms, 8);

        for (int i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            page.IfdOffset = ms.Position;

            var tags = BuildPageTags(
                page,
                width,
                pageHeight,
                bands,
                bytesPerSample,
                description: i == 0 ? description : null,
                orientation: i == 0 ? orientation : null,
                icc: i == 0 ? icc : null,
                xmp: i == 0 ? xmp : null);

            WriteU16(ms, checked((ushort)tags.Count));
            foreach (var tag in tags)
            {
                WriteU16(ms, tag.Tag);
                WriteU16(ms, tag.Type);
                WriteU32(ms, checked((uint)tag.Count));

                tag.ValueFieldPosition = ms.Position;
                if (tag.ExternalData == null)
                {
                    if (tag.PatchKind == PatchKind.SingleChunkOffset)
                        page.SingleChunkOffsetValuePosition = tag.ValueFieldPosition;

                    var inline = tag.InlineData ?? Array.Empty<byte>();
                    ms.Write(inline, 0, inline.Length);
                    for (int pad = inline.Length; pad < 4; pad++)
                        ms.WriteByte(0);
                }
                else
                {
                    WriteU32(ms, 0);
                }
            }

            page.NextIfdPointerPosition = ms.Position;
            WriteU32(ms, 0);

            foreach (var tag in tags)
            {
                if (tag.ExternalData == null)
                    continue;

                AlignEven(ms);
                tag.ExternalDataOffset = ms.Position;
                PatchU32(ms, tag.ValueFieldPosition, checked((uint)tag.ExternalDataOffset));

                if (tag.PatchKind == PatchKind.ChunkOffsetsArray)
                {
                    page.ChunkOffsetsArrayPosition = ms.Position;
                }

                ms.Write(tag.ExternalData, 0, tag.ExternalData.Length);
            }
        }

        for (int i = 0; i < pages.Count; i++)
        {
            uint next = i + 1 < pages.Count
                ? checked((uint)pages[i + 1].IfdOffset)
                : 0u;
            PatchU32(ms, pages[i].NextIfdPointerPosition, next);
        }

        foreach (var page in pages)
        {
            for (int i = 0; i < page.Chunks.Count; i++)
            {
                AlignEven(ms);
                long chunkOffset = ms.Position;
                ms.Write(page.Chunks[i], 0, page.Chunks[i].Length);

                if (page.Chunks.Count == 1)
                {
                    PatchU32(ms, page.SingleChunkOffsetValuePosition, checked((uint)chunkOffset));
                }
                else
                {
                    PatchU32(ms, page.ChunkOffsetsArrayPosition + i * 4, checked((uint)chunkOffset));
                }
            }
        }

        return ms.ToArray();
    }

    private static List<PageData> BuildPages(
        byte[] pixels,
        int width,
        int pageHeight,
        int nPages,
        int bands,
        int bytesPerSample,
        int tileSize)
    {
        var pages = new List<PageData>(nPages);
        int rowStride = width * bands * bytesPerSample;
        int frameSize = rowStride * pageHeight;

        for (int pageIndex = 0; pageIndex < nPages; pageIndex++)
        {
            int frameOffset = pageIndex * frameSize;
            var page = new PageData
            {
                IsTiled = tileSize > 0,
                TileSize = tileSize,
                RowsPerStrip = pageHeight,
            };

            if (tileSize > 0)
            {
                int tilesAcross = (width + tileSize - 1) / tileSize;
                int tilesDown = (pageHeight + tileSize - 1) / tileSize;
                int tileRowStride = tileSize * bands * bytesPerSample;
                int tileBytes = tileRowStride * tileSize;

                for (int ty = 0; ty < tilesDown; ty++)
                {
                    for (int tx = 0; tx < tilesAcross; tx++)
                    {
                        var tile = new byte[tileBytes];
                        int copyRows = Math.Min(tileSize, pageHeight - ty * tileSize);
                        int copyCols = Math.Min(tileSize, width - tx * tileSize);
                        int copyBytes = copyCols * bands * bytesPerSample;

                        for (int row = 0; row < copyRows; row++)
                        {
                            int srcOffset = frameOffset
                                + ((ty * tileSize + row) * rowStride)
                                + tx * tileSize * bands * bytesPerSample;
                            Buffer.BlockCopy(pixels, srcOffset, tile, row * tileRowStride, copyBytes);
                        }

                        page.Chunks.Add(CompressZlib(tile));
                    }
                }
            }
            else
            {
                var strip = new byte[frameSize];
                Buffer.BlockCopy(pixels, frameOffset, strip, 0, frameSize);
                page.Chunks.Add(CompressZlib(strip));
            }

            pages.Add(page);
        }

        return pages;
    }

    private static List<TagRecord> BuildPageTags(
        PageData page,
        int width,
        int pageHeight,
        int bands,
        int bytesPerSample,
        string? description,
        int? orientation,
        byte[]? icc,
        byte[]? xmp)
    {
        var tags = new List<TagRecord>();
        int bitsPerSample = bytesPerSample * 8;
        ushort photometric = (ushort)(bands == 1 ? 1 : 2);
        uint compression = 8;

        AddLong(tags, 256, (uint)width);
        AddLong(tags, 257, (uint)pageHeight);
        AddBitsPerSample(tags, bands, bitsPerSample);
        AddShort(tags, 259, (ushort)compression);
        AddShort(tags, 262, photometric);
        if (!string.IsNullOrEmpty(description))
            AddBytes(tags, 270, 2, Encoding.UTF8.GetBytes(description + "\0"));

        if (page.IsTiled)
        {
            AddLong(tags, 322, (uint)page.TileSize);
            AddLong(tags, 323, (uint)page.TileSize);
            AddChunkOffsets(tags, 324, page);
        }
        else
        {
            AddChunkOffsets(tags, 273, page);
        }

        AddShort(tags, 277, (ushort)bands);
        if (!page.IsTiled)
            AddLong(tags, 278, (uint)page.RowsPerStrip);
        AddChunkByteCounts(tags, page.IsTiled ? (ushort)325 : (ushort)279, page);
        AddShort(tags, 284, 1);
        if (orientation.HasValue)
            AddShort(tags, 274, checked((ushort)orientation.Value));
        if (xmp is { Length: > 0 })
            AddBytes(tags, 700, 1, xmp);
        if (icc is { Length: > 0 })
            AddBytes(tags, 34675, 7, icc);

        tags.Sort(static (a, b) => a.Tag.CompareTo(b.Tag));
        return tags;
    }

    private static void AddBitsPerSample(List<TagRecord> tags, int bands, int bitsPerSample)
    {
        var data = new byte[bands * 2];
        for (int i = 0; i < bands; i++)
            WriteU16(data, i * 2, checked((ushort)bitsPerSample));
        AddInlineOrExternal(tags, 258, 3, (uint)bands, data);
    }

    private static void AddChunkOffsets(List<TagRecord> tags, ushort tag, PageData page)
    {
        if (page.Chunks.Count == 1)
        {
            var record = new TagRecord
            {
                Tag = tag,
                Type = 4,
                Count = 1,
                InlineData = new byte[4],
                PatchKind = PatchKind.SingleChunkOffset,
            };
            tags.Add(record);
            return;
        }

        var data = new byte[page.Chunks.Count * 4];
        var recordArray = new TagRecord
        {
            Tag = tag,
            Type = 4,
            Count = (uint)page.Chunks.Count,
            ExternalData = data,
            PatchKind = PatchKind.ChunkOffsetsArray,
        };
        tags.Add(recordArray);
    }

    private static void AddChunkByteCounts(List<TagRecord> tags, ushort tag, PageData page)
    {
        if (page.Chunks.Count == 1)
        {
            AddLong(tags, tag, (uint)page.Chunks[0].Length);
            return;
        }

        var data = new byte[page.Chunks.Count * 4];
        for (int i = 0; i < page.Chunks.Count; i++)
            WriteU32(data, i * 4, checked((uint)page.Chunks[i].Length));
        tags.Add(new TagRecord
        {
            Tag = tag,
            Type = 4,
            Count = (uint)page.Chunks.Count,
            ExternalData = data,
        });
    }

    private static void AddShort(List<TagRecord> tags, ushort tag, ushort value)
    {
        var data = new byte[2];
        WriteU16(data, 0, value);
        tags.Add(new TagRecord { Tag = tag, Type = 3, Count = 1, InlineData = data });
    }

    private static void AddLong(List<TagRecord> tags, ushort tag, uint value)
    {
        var data = new byte[4];
        WriteU32(data, 0, value);
        tags.Add(new TagRecord { Tag = tag, Type = 4, Count = 1, InlineData = data });
    }

    private static void AddBytes(List<TagRecord> tags, ushort tag, ushort type, byte[] data)
        => AddInlineOrExternal(tags, tag, type, checked((uint)data.Length), data);

    private static void AddInlineOrExternal(List<TagRecord> tags, ushort tag, ushort type, uint count, byte[] data)
    {
        if (data.Length <= 4)
        {
            tags.Add(new TagRecord { Tag = tag, Type = type, Count = count, InlineData = data });
        }
        else
        {
            tags.Add(new TagRecord { Tag = tag, Type = type, Count = count, ExternalData = data });
        }
    }

    private static byte[] CompressZlib(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static void AlignEven(Stream stream)
    {
        if ((stream.Position & 1L) != 0)
            stream.WriteByte(0);
    }

    private static void PatchU32(Stream stream, long position, uint value)
    {
        long restore = stream.Position;
        stream.Position = position;
        WriteU32(stream, value);
        stream.Position = restore;
    }

    private static void WriteU16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
    }

    private static void WriteU32(Stream stream, uint value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 24));
    }

    private static void WriteU16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteU32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private sealed class PageData
    {
        public bool IsTiled;
        public int TileSize;
        public int RowsPerStrip;
        public readonly List<byte[]> Chunks = new();
        public long IfdOffset;
        public long NextIfdPointerPosition;
        public long SingleChunkOffsetValuePosition;
        public long ChunkOffsetsArrayPosition;
    }

    private sealed class TagRecord
    {
        public ushort Tag;
        public ushort Type;
        public uint Count;
        public byte[]? InlineData;
        public byte[]? ExternalData;
        public PatchKind PatchKind;
        public long ValueFieldPosition;
        public long ExternalDataOffset;
    }

    private enum PatchKind
    {
        None,
        SingleChunkOffset,
        ChunkOffsetsArray,
    }
}
