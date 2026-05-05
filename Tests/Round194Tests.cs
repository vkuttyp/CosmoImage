using System;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 194 — FITS NAXIS≥4 height-stacked layout. Same convention
/// as NIfTI 4D (round 193): NAXIS3·NAXIS4 frames stack into the
/// height axis with <c>n-pages</c> / <c>page-height</c> /
/// <c>fits:naxis3</c> / <c>fits:naxis4</c> metadata. NAXIS=2 and
/// NAXIS=3 with NAXIS3 ∈ {1,3,4} keep the legacy bands-as-channels
/// layout.
/// </summary>
public class Round194Tests
{
    private const int BlockSize = 2880;
    private const int CardSize = 80;

    /// <summary>
    /// Build a minimal FITS file with the given NAXIS-N values and
    /// pixel bytes. BITPIX=8 (uint8) for simple test fixtures.
    /// </summary>
    private static byte[] BuildFits(int n1, int n2, int n3, int n4, byte[] pixelBytes)
    {
        int naxis = n4 > 1 ? 4 : (n3 > 1 ? 3 : 2);
        var cards = new System.Collections.Generic.List<string>
        {
            FormatLogicalCard("SIMPLE", "T"),
            FormatIntCard("BITPIX", 8),
            FormatIntCard("NAXIS", naxis),
            FormatIntCard("NAXIS1", n1),
            FormatIntCard("NAXIS2", n2),
        };
        if (naxis >= 3) cards.Add(FormatIntCard("NAXIS3", n3));
        if (naxis >= 4) cards.Add(FormatIntCard("NAXIS4", n4));
        cards.Add("END".PadRight(CardSize));

        // Pad headers to a 2880-byte boundary.
        var header = new byte[BlockSize * ((cards.Count * CardSize + BlockSize - 1) / BlockSize)];
        Array.Fill(header, (byte)' ');
        int p = 0;
        foreach (var card in cards)
        {
            var b = Encoding.ASCII.GetBytes(card);
            Buffer.BlockCopy(b, 0, header, p, CardSize);
            p += CardSize;
        }

        // Pixel data follows; for tests we don't bother padding the data
        // section to a 2880 boundary (the loader doesn't require it).
        var bytes = new byte[header.Length + pixelBytes.Length];
        Buffer.BlockCopy(header, 0, bytes, 0, header.Length);
        Buffer.BlockCopy(pixelBytes, 0, bytes, header.Length, pixelBytes.Length);
        return bytes;
    }

    private static string FormatIntCard(string keyword, int value)
        => $"{keyword,-8}= {value,20}".PadRight(CardSize);

    private static string FormatLogicalCard(string keyword, string value)
        => $"{keyword,-8}= {value,20}".PadRight(CardSize);

    private static async Task<VipsImage> LoadAsync(byte[] bytes)
    {
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        var img = await VipsFitsLoader.LoadAsync(src);
        Assert.NotNull(img);
        return img!;
    }

    private static byte ReadPel(VipsImage img, int x, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        return reg.GetAddress(x, y)[0];
    }

    [Fact]
    public async Task Naxis4_StacksFramesIntoHeight()
    {
        // 3×2×2×3 cube: 6 frames stacked, each 3×2.
        const int n1 = 3, n2 = 2, n3 = 2, n4 = 3;
        var pixels = new byte[n1 * n2 * n3 * n4];
        for (int t = 0; t < n4; t++)
            for (int z = 0; z < n3; z++)
            {
                int frame = t * n3 + z;
                for (int i = 0; i < n1 * n2; i++)
                    pixels[frame * n1 * n2 + i] = (byte)frame;
            }

        var fits = BuildFits(n1, n2, n3, n4, pixels);
        var img = await LoadAsync(fits);

        Assert.Equal(1, img.Bands);
        Assert.Equal(n1, img.Width);
        Assert.Equal(n2 * n3 * n4, img.Height);

        Assert.Equal("2", img.Metadata["fits:naxis3"]);
        Assert.Equal("3", img.Metadata["fits:naxis4"]);
        Assert.Equal("6", img.Metadata["n-pages"]);
        Assert.Equal("2", img.Metadata["page-height"]);

        // Each frame's stacked rows hold the frame index.
        for (int frame = 0; frame < n3 * n4; frame++)
        {
            int yBase = frame * n2;
            for (int y = 0; y < n2; y++)
                for (int x = 0; x < n1; x++)
                    Assert.Equal((byte)frame, ReadPel(img, x, yBase + y));
        }
    }

    [Fact]
    public async Task Naxis3_LargeN3_StacksFramesIntoHeight()
    {
        // NAXIS3 = 8 (not in {1, 3, 4}) → height-stacked layout.
        const int n1 = 4, n2 = 3, n3 = 8;
        var pixels = new byte[n1 * n2 * n3];
        for (int z = 0; z < n3; z++)
            for (int i = 0; i < n1 * n2; i++)
                pixels[z * n1 * n2 + i] = (byte)z;

        var fits = BuildFits(n1, n2, n3, 1, pixels);
        var img = await LoadAsync(fits);

        Assert.Equal(1, img.Bands);
        Assert.Equal(n2 * n3, img.Height);
        Assert.Equal("8", img.Metadata["fits:naxis3"]);
        Assert.Equal("8", img.Metadata["n-pages"]);
        Assert.Equal(0, ReadPel(img, 0, 0));
        Assert.Equal(7, ReadPel(img, 0, img.Height - 1));
    }

    [Fact]
    public async Task Naxis3_RgbCube_KeepsLegacyBandsLayout()
    {
        // NAXIS3 = 3 → standard RGB cube. Legacy bands-as-channels path.
        const int n1 = 2, n2 = 2, n3 = 3;
        var pixels = new byte[n1 * n2 * n3];
        // Plane 0 = red (255), plane 1 = green (128), plane 2 = blue (64).
        for (int i = 0; i < n1 * n2; i++) pixels[i] = 255;
        for (int i = 0; i < n1 * n2; i++) pixels[n1 * n2 + i] = 128;
        for (int i = 0; i < n1 * n2; i++) pixels[2 * n1 * n2 + i] = 64;

        var fits = BuildFits(n1, n2, n3, 1, pixels);
        var img = await LoadAsync(fits);

        Assert.Equal(3, img.Bands);
        Assert.Equal(n2, img.Height);
        Assert.False(img.Metadata.ContainsKey("fits:naxis3"));
        Assert.False(img.Metadata.ContainsKey("n-pages"));

        // Verify R/G/B at any pixel.
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, n1, n2));
        var addr = reg.GetAddress(0, 0);
        Assert.Equal(255, addr[0]);
        Assert.Equal(128, addr[1]);
        Assert.Equal(64, addr[2]);
    }

    [Fact]
    public async Task Naxis2_KeepsLegacyLayout()
    {
        const int n1 = 4, n2 = 3;
        var pixels = new byte[n1 * n2];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 10);

        var fits = BuildFits(n1, n2, 1, 1, pixels);
        var img = await LoadAsync(fits);

        Assert.Equal(1, img.Bands);
        Assert.Equal(n2, img.Height);
        Assert.False(img.Metadata.ContainsKey("fits:naxis3"));
    }
}
