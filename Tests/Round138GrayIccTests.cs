using CosmoImage.Core;
using CosmoImage.Operations.Color;
using CosmoImage.Operations.Metadata;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Coverage for the gray-input branch of VipsIccTransform (task #27): when
/// a 1-band or 2-band image hits a matrix/TRC RGB profile, replicate the
/// gray channel to all three colour channels before the matrix multiply
/// instead of throwing NotSupportedException.
/// </summary>
public class Round138GrayIccTests
{
    /// <summary>Build a minimal identity matrix/TRC RGB profile (gamma 1).
    /// Primaries placed so source = destination = identity transform.</summary>
    private static byte[] BuildIdentityMatrixProfile()
    {
        var prof = new VipsIccProfile
        {
            DataColorSpace = VipsIccColorSpace.Rgb,
            ConnectionColorSpace = VipsIccColorSpace.Xyz,
        };
        // sRGB-style primaries in D50 XYZ.
        prof.SetTagXyz("rXYZ", new VipsColorXyz(0.4361, 0.2225, 0.0139));
        prof.SetTagXyz("gXYZ", new VipsColorXyz(0.3851, 0.7169, 0.0971));
        prof.SetTagXyz("bXYZ", new VipsColorXyz(0.1431, 0.0606, 0.7141));
        // Gamma 1 = identity TRC; sufficient for verifying the band-count
        // routing without dragging in sRGB's pseudo-curve.
        prof.SetTagCurveGamma("rTRC", 1.0);
        prof.SetTagCurveGamma("gTRC", 1.0);
        prof.SetTagCurveGamma("bTRC", 1.0);
        return prof.ToBytes();
    }

    [Fact]
    public void GrayInput_MatrixProfile_PromotesToThreeBand()
    {
        // Identity matrix-TRC source = destination so we can verify the
        // promotion produced (G, G, G) for each gray pixel.
        var profBytes = BuildIdentityMatrixProfile();
        var pixels = new byte[] { 0, 64, 128, 192, 255 };  // 5×1 gray strip
        var img = new VipsImage
        {
            Width = 5, Height = 1, Bands = 1,
            BandFormat = VipsBandFormat.UChar,
            Coding = VipsCoding.None,
            Interpretation = VipsInterpretation.BW,
            XRes = 1.0, YRes = 1.0,
            PixelsLazy = new System.Lazy<byte[]>(() => pixels),
        };

        var t = new VipsIccTransform
        {
            In = img,
            InputProfile = profBytes,
            OutputProfile = profBytes,
        };
        Assert.Equal(0, t.Build());
        Assert.NotNull(t.Out);
        Assert.Equal(3, t.Out!.Bands);  // promoted to RGB
        Assert.Equal(5, t.Out.Width);

        using var reg = new VipsRegion(t.Out);
        Assert.Equal(0, reg.Prepare(new VipsRect(0, 0, 5, 1)));
        var row = reg.GetAddress(0, 0);
        for (int x = 0; x < 5; x++)
        {
            byte g = pixels[x];
            // Identity profile → (G, G, G). Allow ±2 for rounding through
            // matrix * identity * matrix-inverse.
            Assert.InRange(row[x * 3 + 0], (byte)System.Math.Max(0, g - 2), (byte)System.Math.Min(255, g + 2));
            Assert.InRange(row[x * 3 + 1], (byte)System.Math.Max(0, g - 2), (byte)System.Math.Min(255, g + 2));
            Assert.InRange(row[x * 3 + 2], (byte)System.Math.Max(0, g - 2), (byte)System.Math.Min(255, g + 2));
        }
    }

    [Fact]
    public void GrayAlphaInput_MatrixProfile_PromotesToFourBandAndKeepsAlpha()
    {
        var profBytes = BuildIdentityMatrixProfile();
        // 3×1 gray+alpha pixels: gray = 100/200/50, alpha = 64/128/255.
        var pixels = new byte[] { 100, 64, 200, 128, 50, 255 };
        var img = new VipsImage
        {
            Width = 3, Height = 1, Bands = 2,
            BandFormat = VipsBandFormat.UChar,
            Coding = VipsCoding.None,
            Interpretation = VipsInterpretation.BW,
            XRes = 1.0, YRes = 1.0,
            PixelsLazy = new System.Lazy<byte[]>(() => pixels),
        };

        var t = new VipsIccTransform { In = img, InputProfile = profBytes, OutputProfile = profBytes };
        Assert.Equal(0, t.Build());
        Assert.Equal(4, t.Out!.Bands);  // promoted to RGBA

        using var reg = new VipsRegion(t.Out);
        reg.Prepare(new VipsRect(0, 0, 3, 1));
        var row = reg.GetAddress(0, 0);
        // Alpha passes through unchanged.
        Assert.Equal(64,  row[0 * 4 + 3]);
        Assert.Equal(128, row[1 * 4 + 3]);
        Assert.Equal(255, row[2 * 4 + 3]);
        // Gray channel replicated to R/G/B (within rounding).
        Assert.InRange(row[0 * 4 + 0], (byte)98,  (byte)102);
        Assert.InRange(row[0 * 4 + 1], (byte)98,  (byte)102);
        Assert.InRange(row[0 * 4 + 2], (byte)98,  (byte)102);
        Assert.InRange(row[1 * 4 + 0], (byte)198, (byte)202);
        Assert.InRange(row[2 * 4 + 0], (byte)48,  (byte)52);
    }
}
