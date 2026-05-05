using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 153 — Radiance HDR non-standard axis orderings. The
/// resolution line names two axes with sign prefixes; the four
/// Y-first orderings are flips of a normal top-to-bottom-left-to-
/// right image. Decoder now applies the right flips post-decode.
/// X-first orderings (rotated images) remain a punt.
/// </summary>
public class Round153Tests
{
    private static IVipsSource Source(byte[] bytes)
        => new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));

    /// <summary>
    /// Hand-build an HDR file with literal RGBE scanlines (no RLE,
    /// width &lt; 8). Lets us control the exact resolution line and
    /// pixel layout per axis ordering.
    /// </summary>
    private static byte[] BuildHdrFile(string resolutionLine, byte[] scanlineBytes)
    {
        var hdr = Encoding.ASCII.GetBytes(
            $"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n{resolutionLine}\n");
        var dst = new byte[hdr.Length + scanlineBytes.Length];
        Buffer.BlockCopy(hdr, 0, dst, 0, hdr.Length);
        Buffer.BlockCopy(scanlineBytes, 0, dst, hdr.Length, scanlineBytes.Length);
        return dst;
    }

    /// <summary>
    /// 2-row × 4-column RGBE pattern where each pixel is identifiable
    /// by its position. Bytes laid out top-to-bottom, left-to-right
    /// (the canonical order); R = x*16, G = y*16, B = (x+y)*8, E = 128.
    /// </summary>
    private static byte[] CanonicalScanline()
    {
        var dst = new byte[2 * 4 * 4];
        int p = 0;
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 4; x++)
            {
                dst[p++] = (byte)(x * 16);
                dst[p++] = (byte)(y * 16);
                dst[p++] = (byte)((x + y) * 8);
                dst[p++] = 128;
            }
        return dst;
    }

    [Fact]
    public async Task Hdr_StandardYneg_Xpos_Decodes()
    {
        var img = await VipsHdrLoader.LoadAsync(Source(BuildHdrFile("-Y 2 +X 4", CanonicalScanline())));
        Assert.NotNull(img);
        AssertCanonicalLayout(img!);
    }

    [Fact]
    public async Task Hdr_Ypos_Xpos_FlipsRows()
    {
        // +Y means scanlines are ordered bottom-to-top in the file.
        // The file's first scanline is the IMAGE's bottom row.
        // Build: the y=0 row (image's top) should be encoded last.
        var swapped = new byte[2 * 4 * 4];
        // File row 0 = image row 1 (y=1 pattern: G=16).
        // File row 1 = image row 0 (y=0 pattern: G=0).
        var canonical = CanonicalScanline();
        Buffer.BlockCopy(canonical, 4 * 4, swapped, 0, 4 * 4);  // image row 1 → file row 0
        Buffer.BlockCopy(canonical, 0, swapped, 4 * 4, 4 * 4);  // image row 0 → file row 1

        var img = await VipsHdrLoader.LoadAsync(Source(BuildHdrFile("+Y 2 +X 4", swapped)));
        Assert.NotNull(img);
        AssertCanonicalLayout(img!);
    }

    [Fact]
    public async Task Hdr_Yneg_Xneg_FlipsColumns()
    {
        // -X means within each row pixels are right-to-left.
        // Build: each row's pixels in reverse order.
        var canonical = CanonicalScanline();
        var reversed = new byte[canonical.Length];
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 4; x++)
                Buffer.BlockCopy(canonical, (y * 4 + (3 - x)) * 4, reversed, (y * 4 + x) * 4, 4);

        var img = await VipsHdrLoader.LoadAsync(Source(BuildHdrFile("-Y 2 -X 4", reversed)));
        Assert.NotNull(img);
        AssertCanonicalLayout(img!);
    }

    [Fact]
    public async Task Hdr_Ypos_Xneg_FlipsBoth()
    {
        // Both axes flipped — "180° rotation" of the canonical layout.
        var canonical = CanonicalScanline();
        var both = new byte[canonical.Length];
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 4; x++)
                Buffer.BlockCopy(canonical, ((1 - y) * 4 + (3 - x)) * 4, both, (y * 4 + x) * 4, 4);

        var img = await VipsHdrLoader.LoadAsync(Source(BuildHdrFile("+Y 2 -X 4", both)));
        Assert.NotNull(img);
        AssertCanonicalLayout(img!);
    }

    [Fact]
    public async Task Hdr_Xfirst_RotatedAxes_StillRejected()
    {
        // X-first orderings would require axis transposition (90° rotation).
        // We deliberately punt — these are vanishingly rare in practice.
        var img = await VipsHdrLoader.LoadAsync(Source(BuildHdrFile("+X 4 -Y 2", CanonicalScanline())));
        Assert.Null(img);
    }

    /// <summary>
    /// Verify the decoded image matches the canonical
    /// top-to-bottom-left-to-right layout from CanonicalScanline.
    /// </summary>
    private static void AssertCanonicalLayout(VipsImage img)
    {
        Assert.Equal(4, img.Width);
        Assert.Equal(2, img.Height);
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 4, 2));
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                float expR = (float)((x * 16 + 0.5) * Math.Pow(2.0, 128 - 136));
                float expG = (float)((y * 16 + 0.5) * Math.Pow(2.0, 128 - 136));
                float expB = (float)(((x + y) * 8 + 0.5) * Math.Pow(2.0, 128 - 136));
                var addr = reg.GetAddress(x, y);
                float r = BinaryPrimitives.ReadSingleLittleEndian(addr.Slice(0, 4));
                float g = BinaryPrimitives.ReadSingleLittleEndian(addr.Slice(4, 4));
                float b = BinaryPrimitives.ReadSingleLittleEndian(addr.Slice(8, 4));
                Assert.Equal(expR, r, 0.001f);
                Assert.Equal(expG, g, 0.001f);
                Assert.Equal(expB, b, 0.001f);
            }
        }
    }
}
