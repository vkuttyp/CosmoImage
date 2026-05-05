using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 182 — pure-C# APNG saver. Drops the last Magick dependency in
/// the APNG path: <see cref="VipsApngSaver"/> now composes per-frame
/// PNGs (from <see cref="VipsPngSaver"/>) into APNG chunks directly.
///
/// <para>Tests verify the wire format (PNG signature, IHDR, acTL, fcTL,
/// IDAT for frame 0, fdAT for frames 1..N-1, IEND) and round-trip via
/// <see cref="VipsPngLoader"/> which uses the existing pure-C#
/// <c>PureApngDecoder</c>.</para>
/// </summary>
public class Round182Tests
{
    /// <summary>
    /// Build a 3-frame stacked-RGBA "animated" image: 4×4 canvas per
    /// frame, frames coloured red / green / blue. Stacked layout
    /// matches the multi-frame convention used by the GIF/WebP/APNG
    /// savers.
    /// </summary>
    private static VipsImage Make3FrameRgba(int frameW, int frameH)
    {
        int totalH = frameH * 3;
        var img = new VipsImage
        {
            Width = frameW, Height = totalH, Bands = 4, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    int gy = reg.Valid.Top + y;
                    int frame = gy / frameH;
                    var addr = reg.GetAddress(reg.Valid.Left, gy);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        // Each frame is a solid colour, alpha=255.
                        addr[x * 4 + 0] = (byte)(frame == 0 ? 255 : 0);
                        addr[x * 4 + 1] = (byte)(frame == 1 ? 255 : 0);
                        addr[x * 4 + 2] = (byte)(frame == 2 ? 255 : 0);
                        addr[x * 4 + 3] = 255;
                    }
                }
                return 0;
            }
        };
        img.Metadata["n-pages"] = "3";
        img.Metadata["page-height"] = frameH.ToString();
        img.Metadata["animation-delays"] = "5,10,15";
        return img;
    }

    private static async Task<byte[]> SaveAsync(VipsImage img)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await VipsApngSaver.SaveAsync(img, writer);
        return ms.ToArray();
    }

    [Fact]
    public async Task SingleFrame_FallsBackToRegularPng()
    {
        // No multi-frame metadata → emit as plain PNG.
        var img = new VipsImage
        {
            Width = 4, Height = 4, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width * 3; x++) addr[x] = 128;
                }
                return 0;
            }
        };
        var bytes = await SaveAsync(img);
        // PNG signature 0x89 0x50 0x4E 0x47
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal(0x50, bytes[1]);
        // Single-frame PNG should NOT contain the APNG-specific acTL chunk.
        Assert.False(ContainsChunk(bytes, "acTL"));
    }

    [Fact]
    public async Task MultiFrame_ContainsRequiredApngChunks()
    {
        var img = Make3FrameRgba(4, 4);
        var bytes = await SaveAsync(img);

        Assert.True(ContainsChunk(bytes, "IHDR"));
        Assert.True(ContainsChunk(bytes, "acTL"));
        Assert.True(ContainsChunk(bytes, "fcTL"));
        Assert.True(ContainsChunk(bytes, "IDAT"));
        Assert.True(ContainsChunk(bytes, "fdAT"));
        Assert.True(ContainsChunk(bytes, "IEND"));

        // acTL must report 3 frames, infinite loops.
        var actl = FirstChunkData(bytes, "acTL");
        Assert.NotNull(actl);
        Assert.Equal((uint)3, BinaryPrimitives.ReadUInt32BigEndian(actl.AsSpan(0, 4)));
        Assert.Equal((uint)0, BinaryPrimitives.ReadUInt32BigEndian(actl.AsSpan(4, 4)));
    }

    [Fact]
    public async Task MultiFrame_FcTLSequenceNumbersAreSequential()
    {
        var img = Make3FrameRgba(4, 4);
        var bytes = await SaveAsync(img);

        // Sequence numbers run across fcTL + fdAT only — frame 0's IDAT
        // does NOT consume a seq. With 3 frames and 1 IDAT/fdAT per frame:
        //   fcTL[0]=seq 0, IDAT(no seq),
        //   fcTL[1]=seq 1, fdAT=seq 2,
        //   fcTL[2]=seq 3, fdAT=seq 4.
        var fcTLs = AllChunkData(bytes, "fcTL");
        Assert.Equal(3, fcTLs.Count);
        Assert.Equal((uint)0, BinaryPrimitives.ReadUInt32BigEndian(fcTLs[0].AsSpan(0, 4)));
        Assert.Equal((uint)1, BinaryPrimitives.ReadUInt32BigEndian(fcTLs[1].AsSpan(0, 4)));
        Assert.Equal((uint)3, BinaryPrimitives.ReadUInt32BigEndian(fcTLs[2].AsSpan(0, 4)));
    }

    [Fact]
    public async Task MultiFrame_FrameDelaysWrittenToFcTL()
    {
        var img = Make3FrameRgba(4, 4);
        var bytes = await SaveAsync(img);

        var fcTLs = AllChunkData(bytes, "fcTL");
        // delay_num lives at offset 20 (after seq + w + h + x + y).
        Assert.Equal(5, BinaryPrimitives.ReadUInt16BigEndian(fcTLs[0].AsSpan(20, 2)));
        Assert.Equal(10, BinaryPrimitives.ReadUInt16BigEndian(fcTLs[1].AsSpan(20, 2)));
        Assert.Equal(15, BinaryPrimitives.ReadUInt16BigEndian(fcTLs[2].AsSpan(20, 2)));
    }

    [Fact]
    public async Task RoundTrip_DecodesViaPngLoader_PreservesFrameContent()
    {
        // Save → load via VipsPngLoader (which routes APNGs through
        // PureApngDecoder). Verify per-frame pixel content.
        var src = Make3FrameRgba(4, 4);
        var bytes = await SaveAsync(src);

        var srcReader = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        var loaded = await VipsPngLoader.LoadAsync(srcReader);
        Assert.NotNull(loaded);

        Assert.Equal("3", loaded!.Metadata["n-pages"]);
        Assert.Equal("4", loaded.Metadata["page-height"]);
        Assert.Equal(12, loaded.Height); // 3 frames × 4 px

        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, loaded.Width, loaded.Height));

        // Frame 0 (rows 0..3): red.
        var f0 = reg.GetAddress(0, 0);
        Assert.Equal(255, f0[0]); Assert.Equal(0, f0[1]); Assert.Equal(0, f0[2]);
        // Frame 1 (rows 4..7): green.
        var f1 = reg.GetAddress(0, 4);
        Assert.Equal(0, f1[0]); Assert.Equal(255, f1[1]); Assert.Equal(0, f1[2]);
        // Frame 2 (rows 8..11): blue.
        var f2 = reg.GetAddress(0, 8);
        Assert.Equal(0, f2[0]); Assert.Equal(0, f2[1]); Assert.Equal(255, f2[2]);
    }

    // ---------- Helpers: trivial PNG chunk walker for assertions ----------

    private static bool ContainsChunk(byte[] png, string type) => FirstChunkData(png, type) != null;

    private static byte[]? FirstChunkData(byte[] png, string type)
    {
        var list = AllChunkData(png, type);
        return list.Count > 0 ? list[0] : null;
    }

    private static System.Collections.Generic.List<byte[]> AllChunkData(byte[] png, string type)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var result = new System.Collections.Generic.List<byte[]>();
        int p = 8;
        while (p + 8 <= png.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p, 4));
            int dataOff = p + 8;
            if (dataOff + length + 4 > png.Length) break;
            bool match = true;
            for (int i = 0; i < 4; i++) if (png[p + 4 + i] != typeBytes[i]) { match = false; break; }
            if (match)
            {
                var body = new byte[length];
                Buffer.BlockCopy(png, dataOff, body, 0, length);
                result.Add(body);
            }
            p = dataOff + length + 4;
        }
        return result;
    }
}
