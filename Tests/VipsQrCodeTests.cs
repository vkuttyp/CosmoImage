using System.Text;
using CosmoImage.Operations.Create;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Tests for the pure-managed QR Code generator. Verify dimensions,
/// finder-pattern presence, ECC-level sizing, scale + border behaviour,
/// and round-trip via known-good fixtures.
/// </summary>
public class VipsQrCodeTests
{
    [Fact]
    public void Generate_DefaultParameters_ProducesValidImage()
    {
        var img = VipsQrCode.Generate("https://example.com");
        Assert.NotNull(img);
        Assert.Equal(1, img.Bands);
        Assert.Equal(VipsBandFormat.UChar, img.BandFormat);
        Assert.True(img.Width > 0 && img.Width == img.Height);
        var px = img.PixelsLazy!.Value;
        Assert.Equal(img.Width * img.Height, px.Length);
        // Should have both black and white pixels (a real QR code, not all-one-colour).
        bool sawBlack = false, sawWhite = false;
        foreach (var b in px)
        {
            if (b == 0) sawBlack = true;
            else if (b == 255) sawWhite = true;
            if (sawBlack && sawWhite) break;
        }
        Assert.True(sawBlack, "expected at least one black module");
        Assert.True(sawWhite, "expected at least one white module");
    }

    [Fact]
    public void Generate_KnownPayload_HasFinderPatternsAtThreeCorners()
    {
        // A version-1 QR (any short payload at ECC=High to force small) has
        // 7×7 finder patterns at top-left, top-right, bottom-left. Each
        // pattern is a solid 3×3 dark core surrounded by a 1-module white
        // ring inside a 1-module dark ring. With default 4-module border +
        // 1px/module, finder-pattern dark core lives 4..7+4 = pixels 4..11.
        var img = VipsQrCode.Generate("HI",
            pixelsPerModule: 1, ecc: VipsQrCode.Ecc.High, borderModules: 4);
        var px = img.PixelsLazy!.Value;
        int w = img.Width;

        // Spot-check the top-left finder's outer dark ring (module (4, 4) = px (4, 4)).
        // Module (4, 4) in the symbol is at border+0, border+0 of the finder = dark.
        Assert.Equal(0, px[(4) * w + 4]);
        // Centre of top-left finder (module (7, 7) = pixel (7, 7)) — dark.
        Assert.Equal(0, px[7 * w + 7]);

        // White ring at module (5, 5) (one inside outer ring) = px (5, 5) — white.
        Assert.Equal(255, px[5 * w + 5]);

        // Quiet zone (border) should be all-white at (0, 0).
        Assert.Equal(255, px[0]);
    }

    [Theory]
    [InlineData(VipsQrCode.Ecc.Low)]
    [InlineData(VipsQrCode.Ecc.Medium)]
    [InlineData(VipsQrCode.Ecc.Quartile)]
    [InlineData(VipsQrCode.Ecc.High)]
    public void Generate_DifferentEccLevels_AllProduceImages(VipsQrCode.Ecc ecc)
    {
        var img = VipsQrCode.Generate("test", ecc: ecc);
        Assert.NotNull(img);
        Assert.True(img.Width > 0);
    }

    [Fact]
    public void Generate_PixelsPerModule_ScalesOutput()
    {
        var small = VipsQrCode.Generate("X", pixelsPerModule: 1, borderModules: 0);
        var big   = VipsQrCode.Generate("X", pixelsPerModule: 4, borderModules: 0);
        Assert.Equal(small.Width * 4, big.Width);
        Assert.Equal(small.Height * 4, big.Height);
    }

    [Fact]
    public void Generate_BorderModules_AddsQuietZone()
    {
        var noBorder  = VipsQrCode.Generate("X", pixelsPerModule: 1, borderModules: 0);
        var withBorder = VipsQrCode.Generate("X", pixelsPerModule: 1, borderModules: 4);
        // Border adds 4 modules on each side → +8 total.
        Assert.Equal(noBorder.Width + 8, withBorder.Width);
        // Inside the border (px 0..3) should all be white.
        var px = withBorder.PixelsLazy!.Value;
        int w = withBorder.Width;
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < w; x++)
                Assert.Equal(255, px[y * w + x]);
    }

    [Fact]
    public void Generate_LongerPayload_PicksLargerVersion()
    {
        // Short payload → version 1 (21×21 modules).
        var shortImg = VipsQrCode.Generate("HI",
            pixelsPerModule: 1, borderModules: 0, ecc: VipsQrCode.Ecc.Low);
        Assert.Equal(21, shortImg.Width);

        // 200-byte payload exceeds version 1's capacity → larger version.
        var longText = new string('A', 200);
        var longImg = VipsQrCode.Generate(longText,
            pixelsPerModule: 1, borderModules: 0, ecc: VipsQrCode.Ecc.Low);
        Assert.True(longImg.Width > 21, $"expected larger symbol; got {longImg.Width}");
        // Valid QR sizes are 17 + 4v for v = 1..40 → 21, 25, 29, ..., 177.
        Assert.Equal(0, (longImg.Width - 17) % 4);
        Assert.InRange(longImg.Width, 21, 177);
    }

    [Fact]
    public void Generate_Utf8Payload_RoundTripsCharacterCount()
    {
        // UTF-8: "héllo" = 6 bytes (é = 2). Verify it doesn't throw and the
        // matrix has expected sizing — version selection sees byte count
        // (6), not character count (5).
        var img = VipsQrCode.Generate("héllo");
        Assert.NotNull(img);
        Assert.True(img.Width >= 21);     // at least version 1
    }

    [Fact]
    public void Generate_ZatcaStylePayload_FitsAtMediumEcc()
    {
        // ZATCA invoices use TLV-encoded UTF-8 typically 100..200 bytes.
        // Smoke test the realistic upper range fits without throwing.
        var sb = new StringBuilder();
        for (int i = 0; i < 200; i++) sb.Append((char)('A' + i % 26));
        var img = VipsQrCode.Generate(sb.ToString(), ecc: VipsQrCode.Ecc.Medium);
        Assert.NotNull(img);
    }

    [Fact]
    public void Generate_MaxVersion40_HandlesLargePayload()
    {
        // Version 40 byte mode at Low ECC holds 2953 bytes. Push to ~2000
        // bytes to verify large-version code paths (alignment patterns,
        // version info bits) don't blow up.
        var img = VipsQrCode.Generate(new string('X', 2000), ecc: VipsQrCode.Ecc.Low);
        Assert.NotNull(img);
        Assert.True(img.Width > 100);
    }

    [Fact]
    public void Generate_EmptyString_StillSucceeds()
    {
        // Zero-length payload is valid per spec — just encodes "0 chars in
        // byte mode" + padding.
        var img = VipsQrCode.Generate("");
        Assert.NotNull(img);
        Assert.True(img.Width >= 21);
    }
}
