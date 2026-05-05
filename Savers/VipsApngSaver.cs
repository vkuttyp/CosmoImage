using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Core;

namespace CosmoImage.Savers;

/// <summary>
/// Animated PNG (APNG) writer — pure-C#, composes per-frame PNG IDATs
/// from <see cref="VipsPngSaver"/> into APNG <c>acTL</c> /
/// <c>fcTL</c> / <c>fdAT</c> chunks. Same multi-frame metadata
/// convention as the GIF / animated WebP savers: image height is
/// <c>N · page-height</c>, with <c>n-pages</c>, <c>page-height</c>,
/// and optional <c>animation-delays</c> in <see cref="VipsImage.Metadata"/>.
/// Falls back to a single-frame PNG when the multi-frame metadata
/// isn't set.
///
/// <para>Wire shape: PNG signature → IHDR (from frame 0) → acTL →
/// per-frame [fcTL → IDAT for frame 0, fcTL → fdAT for frames 1..N-1]
/// → IEND. APNG chunk types: <c>acTL</c> (animation control),
/// <c>fcTL</c> (frame control), <c>fdAT</c> (frame data — same as
/// IDAT but with a 4-byte sequence-number prefix).</para>
///
/// <para>All frames use <c>dispose_op=0</c> (NONE — leave canvas alone)
/// and <c>blend_op=1</c> (SOURCE — overwrite). The all-frames-animated
/// variant: every frame including frame 0 participates in the
/// animation. Loops infinitely (<c>num_plays=0</c>).</para>
/// </summary>
public static class VipsApngSaver
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static async Task SaveAsync(VipsImage image, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        int width = image.Width;
        int height = image.Height;
        int bands = image.Bands;

        if (bands != 1 && bands != 3 && bands != 4)
            throw new NotSupportedException($"APNG save needs 1, 3, or 4 bands; got {bands}");

        // Detect multi-frame layout. Default to single-frame.
        int nPages = 1;
        int pageHeight = height;
        if (image.Metadata.TryGetValue("n-pages", out var npStr) &&
            int.TryParse(npStr, out int nP) && nP > 0 &&
            image.Metadata.TryGetValue("page-height", out var phStr) &&
            int.TryParse(phStr, out int ph) && ph > 0 &&
            nP * ph == height)
        {
            nPages = nP;
            pageHeight = ph;
        }

        // Single-frame: the PNG saver handles everything.
        if (nPages == 1)
        {
            await VipsPngSaver.SaveAsync(image, writer, palette: null, cancellationToken);
            return;
        }

        // Per-frame delays (centiseconds). Default 10 cs ≈ 100 ms each.
        var delays = new uint[nPages];
        Array.Fill(delays, 10u);
        if (image.Metadata.TryGetValue("animation-delays", out var dStr))
        {
            var parts = dStr.Split(',');
            for (int i = 0; i < Math.Min(parts.Length, nPages); i++)
                if (uint.TryParse(parts[i], out var d)) delays[i] = d;
        }

        // Encode each frame as a standalone single-frame PNG, then
        // surgically reuse its IHDR + IDATs in the APNG output.
        var framePngs = new byte[nPages][];
        for (int p = 0; p < nPages; p++)
        {
            var frame = image.ExtractArea(0, p * pageHeight, width, pageHeight);
            using var ms = new MemoryStream();
            var fw = PipeWriter.Create(ms);
            await VipsPngSaver.SaveAsync(frame, fw, palette: null, cancellationToken);
            framePngs[p] = ms.ToArray();
        }

        // Compose APNG output. We stream into a memory buffer first so
        // the chunk CRCs land on accurate byte windows; the buffer is
        // then handed off to the writer in one pass.
        using var output = new MemoryStream();
        output.Write(PngSignature);

        // IHDR copied verbatim from frame 0 — every frame has identical
        // dims / colour type / bit depth so this is the canonical IHDR.
        var ihdrChunk = ExtractChunk(framePngs[0], "IHDR")
            ?? throw new InvalidOperationException("Frame 0 PNG missing IHDR");
        WriteChunk(output, "IHDR", ihdrChunk);

        // acTL: 8 bytes — num_frames (uint32 BE) + num_plays (uint32 BE = 0 for infinite).
        var actl = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(actl.AsSpan(0, 4), (uint)nPages);
        BinaryPrimitives.WriteUInt32BigEndian(actl.AsSpan(4, 4), 0);
        WriteChunk(output, "acTL", actl);

        // Sequence numbers run sequentially across fcTL + fdAT chunks
        // per spec; spec validator rejects gaps or duplicates.
        uint seq = 0;

        // Frame 0: fcTL + IDAT(s) from frame 0's PNG.
        WriteChunk(output, "fcTL", BuildFcTL(seq++, (uint)width, (uint)pageHeight,
            xOffset: 0, yOffset: 0, delayNum: (ushort)delays[0], delayDen: 100));
        foreach (var idat in IterateChunks(framePngs[0], "IDAT"))
            WriteChunk(output, "IDAT", idat);

        // Frames 1..N-1: fcTL + fdAT(s). fdAT data = sequence_number prefix + IDAT body.
        for (int p = 1; p < nPages; p++)
        {
            WriteChunk(output, "fcTL", BuildFcTL(seq++, (uint)width, (uint)pageHeight,
                xOffset: 0, yOffset: 0, delayNum: (ushort)delays[p], delayDen: 100));

            foreach (var idat in IterateChunks(framePngs[p], "IDAT"))
            {
                var fdat = new byte[4 + idat.Length];
                BinaryPrimitives.WriteUInt32BigEndian(fdat.AsSpan(0, 4), seq++);
                Buffer.BlockCopy(idat, 0, fdat, 4, idat.Length);
                WriteChunk(output, "fdAT", fdat);
            }
        }

        // IEND from frame 0 has no data; emit our own to keep things obvious.
        WriteChunk(output, "IEND", Array.Empty<byte>());

        var bytes = output.ToArray();
        await writer.AsStream().WriteAsync(bytes, cancellationToken);
        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    /// <summary>
    /// Build an fcTL chunk body. Layout per APNG spec §3.2:
    /// <list type="number">
    ///   <item>sequence_number (uint32 BE) — global counter shared with fdAT.</item>
    ///   <item>width / height (uint32 BE).</item>
    ///   <item>x_offset / y_offset (uint32 BE) — sub-rect within canvas.</item>
    ///   <item>delay_num / delay_den (uint16 BE) — frame delay as a ratio.</item>
    ///   <item>dispose_op / blend_op (uint8 each).</item>
    /// </list>
    /// We emit dispose=0 (NONE), blend=1 (SOURCE) — full-canvas frames,
    /// each frame replaces the previous.
    /// </summary>
    private static byte[] BuildFcTL(uint seq, uint width, uint height,
        uint xOffset, uint yOffset, ushort delayNum, ushort delayDen)
    {
        var buf = new byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), seq);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), height);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12, 4), xOffset);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(16, 4), yOffset);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(20, 2), delayNum);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(22, 2), delayDen);
        buf[24] = 0; // dispose_op = APNG_DISPOSE_OP_NONE
        buf[25] = 1; // blend_op = APNG_BLEND_OP_SOURCE
        return buf;
    }

    /// <summary>
    /// Extract the body of the first chunk matching <paramref name="chunkType"/>
    /// from a PNG byte stream. Returns null if not found.
    /// </summary>
    private static byte[]? ExtractChunk(byte[] png, string chunkType)
    {
        foreach (var data in IterateChunks(png, chunkType)) return data;
        return null;
    }

    /// <summary>
    /// Iterate chunk bodies (excluding length / type / CRC framing) of
    /// the given type. Used to gather all IDATs per frame.
    /// </summary>
    private static IEnumerable<byte[]> IterateChunks(byte[] png, string chunkType)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(chunkType);
        int p = 8; // skip PNG signature
        while (p + 8 <= png.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p, 4));
            int typeOff = p + 4;
            int dataOff = p + 8;
            if (dataOff + length + 4 > png.Length) yield break;
            bool match = true;
            for (int i = 0; i < 4; i++) if (png[typeOff + i] != typeBytes[i]) { match = false; break; }
            if (match)
            {
                var body = new byte[length];
                Buffer.BlockCopy(png, dataOff, body, 0, length);
                yield return body;
            }
            p = dataOff + length + 4; // advance past CRC
        }
    }

    /// <summary>
    /// Emit a PNG-style chunk: 4-byte length + 4-byte type + data + 4-byte CRC32.
    /// CRC is computed over (type + data).
    /// </summary>
    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);

        var crc = new Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc.GetCurrentHashAsUInt32());
        s.Write(crcBytes);
    }
}
