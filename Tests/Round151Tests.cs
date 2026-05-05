using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 151 — APNG IDAT-as-fallback layout. The previous decoder
/// rejected APNGs where IDAT chunks preceded the first fcTL, even
/// though the spec explicitly allows it (IDATs become frame 0's
/// pixel data so static PNG viewers see a valid first-frame image).
/// </summary>
public class Round151Tests
{
    private static VipsImage TwoFrameRgba(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h * 2, Bands = 4, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    int gy = reg.Valid.Top + y;
                    int frame = gy / h;
                    int yInFrame = gy % h;
                    var addr = reg.GetAddress(reg.Valid.Left, gy);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        addr[x * 4]     = (byte)((frame * 80 + gx * 5) & 0xFF);
                        addr[x * 4 + 1] = (byte)((yInFrame * 13) & 0xFF);
                        addr[x * 4 + 2] = (byte)((frame * 40 + gx) & 0xFF);
                        addr[x * 4 + 3] = 255;
                    }
                }
                return 0;
            },
            Metadata = { ["n-pages"] = "2", ["page-height"] = h.ToString(), ["animation-delays"] = "10,10" },
        };

    [Fact]
    public async Task PureApngDecoder_IdatAsFallbackLayout_DecodesAsFrame0()
    {
        // Generate a normal APNG, then surgically rearrange chunks to
        // produce the IDAT-as-fallback layout: move all IDAT chunks to
        // immediately after IHDR (before the first fcTL). The IDATs
        // are the same bytes either way; only chunk order changes.
        int w = 8, h = 6;
        var src = TwoFrameRgba(w, h);
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await VipsApngSaver.SaveAsync(src, writer);
        await writer.CompleteAsync();
        var normalApng = ms.ToArray();

        var rearranged = MoveIdatsBeforeFirstFcTl(normalApng);

        // Sanity: rearrangement preserved chunk count.
        Assert.Equal(CountChunks(normalApng), CountChunks(rearranged));

        var result = PureApngDecoder.TryDecode(rearranged);
        Assert.NotNull(result);
        Assert.Equal(w, result!.CanvasWidth);
        Assert.Equal(h, result.CanvasHeight);
        Assert.Equal(2, result.FrameCount);
        // Pixel buffer = 8 × (6 × 2) × 4 bytes for 2 stacked RGBA frames.
        Assert.Equal(w * h * 2 * 4, result.Pixels.Length);
    }

    [Fact]
    public async Task PureApngDecoder_NormalLayout_StillDecodes()
    {
        // Existing layout (fcTL before IDAT) must continue to work.
        int w = 8, h = 6;
        var src = TwoFrameRgba(w, h);
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await VipsApngSaver.SaveAsync(src, writer);
        await writer.CompleteAsync();
        var bytes = ms.ToArray();

        var result = PureApngDecoder.TryDecode(bytes);
        Assert.NotNull(result);
        Assert.Equal(2, result!.FrameCount);
    }

    /// <summary>
    /// Take an APNG with the standard fcTL-before-IDAT layout and
    /// move all IDAT chunks immediately after IHDR (before the first
    /// fcTL). Each chunk's CRC is part of the chunk itself, so the
    /// rearrangement doesn't require recomputation.
    /// </summary>
    private static byte[] MoveIdatsBeforeFirstFcTl(byte[] apng)
    {
        var (chunks, signaturePrefix) = SplitChunks(apng);

        // Find the IHDR (must be first per spec) and the IDATs.
        int ihdrIdx = chunks.FindIndex(c => c.Type == 0x49484452);
        var idats = chunks.Where(c => c.Type == 0x49444154).ToList();
        if (ihdrIdx < 0 || idats.Count == 0)
            throw new InvalidOperationException("expected IHDR + IDAT chunks");

        // Drop IDATs from their original positions, re-insert right
        // after IHDR (which means: after acTL too, normally — but
        // we want them BEFORE the first fcTL specifically).
        chunks.RemoveAll(c => c.Type == 0x49444154);
        int insertAt = ihdrIdx + 1;
        // Keep acTL before the IDATs (acTL is positioned before IDATs
        // anyway in valid APNGs and must precede them).
        while (insertAt < chunks.Count && chunks[insertAt].Type == 0x6163544C) insertAt++;
        chunks.InsertRange(insertAt, idats);

        return ReassembleChunks(signaturePrefix, chunks);
    }

    private static int CountChunks(byte[] apng) => SplitChunks(apng).chunks.Count;

    private record Chunk(uint Type, byte[] Raw);  // raw includes length+type+data+crc

    private static (List<Chunk> chunks, byte[] signaturePrefix) SplitChunks(byte[] apng)
    {
        var chunks = new List<Chunk>();
        int p = 8;  // skip PNG signature
        while (p + 8 <= apng.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(apng.AsSpan(p, 4));
            uint type = BinaryPrimitives.ReadUInt32BigEndian(apng.AsSpan(p + 4, 4));
            int total = 12 + (int)length;  // length(4) + type(4) + data(length) + crc(4)
            if (p + total > apng.Length) break;
            var raw = new byte[total];
            Buffer.BlockCopy(apng, p, raw, 0, total);
            chunks.Add(new Chunk(type, raw));
            p += total;
            if (type == 0x49454E44) break;  // IEND
        }
        var prefix = new byte[8];
        Buffer.BlockCopy(apng, 0, prefix, 0, 8);
        return (chunks, prefix);
    }

    private static byte[] ReassembleChunks(byte[] signaturePrefix, List<Chunk> chunks)
    {
        using var ms = new MemoryStream();
        ms.Write(signaturePrefix);
        foreach (var c in chunks) ms.Write(c.Raw);
        return ms.ToArray();
    }
}
