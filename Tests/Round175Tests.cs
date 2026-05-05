using System;
using System.Buffers.Binary;
using CosmoImage.Operations.Color;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 175 — CICP-tagged HDR/SDR → scRGB decode. Validates each transfer
/// function (PQ, HLG, BT.709, Linear) against published reference values
/// and the BT.2020 → BT.709 primaries matrix at known primary colours.
/// Uses <see cref="VipsCicp2scRGB.InverseTransfer"/> directly for the
/// numeric checks where pixel-format normalisation would obscure the
/// transfer-function math.
/// </summary>
public class Round175Tests
{
    // ---------- Transfer-function unit tests (closed-form) ----------

    [Fact]
    public void Linear_IsIdentity()
    {
        for (double e = 0.0; e <= 1.0; e += 0.1)
            Assert.Equal(e, VipsCicp2scRGB.InverseTransfer(e, VipsCicpTransfer.Linear), 1e-9);
    }

    [Fact]
    public void PQ_PeakSignal_ReturnsHdrPeak()
    {
        // E'=1 (full PQ code) → 10000 cd/m² absolute, divided by 100 to
        // align SDR diffuse white with scRGB ≈ 1.0. So result ≈ 100.
        double v = VipsCicp2scRGB.InverseTransfer(1.0, VipsCicpTransfer.PQ);
        Assert.InRange(v, 99.9, 100.1);
    }

    [Fact]
    public void PQ_Zero_ReturnsZero()
    {
        Assert.Equal(0.0, VipsCicp2scRGB.InverseTransfer(0.0, VipsCicpTransfer.PQ), 1e-6);
    }

    [Fact]
    public void PQ_PublishedSampleAtMidGrey()
    {
        // From SMPTE ST 2084 reference: 100 cd/m² (SDR diffuse white)
        // encodes to a PQ code of approximately 0.508. Decoding 0.508
        // should return ~1 (after the /100 scaling).
        double v = VipsCicp2scRGB.InverseTransfer(0.508, VipsCicpTransfer.PQ);
        Assert.InRange(v, 0.95, 1.05);
    }

    [Fact]
    public void HLG_HalfSignalBreakpoint()
    {
        // BT.2100 HLG inverse-OETF at E'=0.5 sits exactly at the
        // piecewise junction: lower half gives e²/3 = 0.25/3 ≈ 0.0833.
        double v = VipsCicp2scRGB.InverseTransfer(0.5, VipsCicpTransfer.HLG);
        Assert.Equal(0.25 / 3.0, v, 1e-9);
    }

    [Fact]
    public void HLG_PeakSignal_LandsAtTwelve()
    {
        // E'=1 maps to scene-referred linear of 1.0 in BT.2100 HLG —
        // wait, peak is 12 in the [0, 12] convention. The formula:
        //   (exp((1 - c)/a) + b) / 12  with a=0.17883277, b≈0.2846, c≈0.5599
        // numerically yields exactly 1.0 at E'=1.
        double v = VipsCicp2scRGB.InverseTransfer(1.0, VipsCicpTransfer.HLG);
        Assert.InRange(v, 0.99, 1.01);
    }

    [Fact]
    public void BT709_LinearSegment_BelowKnee()
    {
        // For E' < 0.081, inverse OETF is E' / 4.5. At E'=0.04:
        // result = 0.04 / 4.5 ≈ 0.00889.
        double v = VipsCicp2scRGB.InverseTransfer(0.04, VipsCicpTransfer.BT709);
        Assert.Equal(0.04 / 4.5, v, 1e-9);
    }

    [Fact]
    public void BT709_GammaSegment_AboveKnee()
    {
        // For E' ≥ 0.081, inverse OETF is ((E'+0.099)/1.099)^(1/0.45).
        // At E'=0.5: ((0.599/1.099))^(1/0.45) ≈ 0.214.
        double v = VipsCicp2scRGB.InverseTransfer(0.5, VipsCicpTransfer.BT709);
        double expected = Math.Pow((0.5 + 0.099) / 1.099, 1.0 / 0.45);
        Assert.Equal(expected, v, 1e-9);
    }

    // ---------- Primaries matrix unit tests ----------

    [Fact]
    public void BT709_Primaries_AreIdentity()
    {
        VipsCicp2scRGB.ApplyPrimariesMatrix(VipsCicpPrimaries.BT709,
            r: 0.3, g: 0.6, b: 0.9, out double rs, out double gs, out double bs);
        Assert.Equal(0.3, rs, 1e-9);
        Assert.Equal(0.6, gs, 1e-9);
        Assert.Equal(0.9, bs, 1e-9);
    }

    [Fact]
    public void BT2020_RedPrimary_MapsToWiderSrgbRed()
    {
        // BT.2020 pure-red (1, 0, 0) → BT.709 sRGB primaries gives an
        // out-of-gamut red in scRGB. The matrix's first row is [1.66, -0.59, -0.07];
        // applied to (1, 0, 0) it picks out (1.66, -0.12, -0.02).
        VipsCicp2scRGB.ApplyPrimariesMatrix(VipsCicpPrimaries.BT2020,
            r: 1.0, g: 0.0, b: 0.0, out double rs, out double gs, out double bs);
        Assert.Equal(1.6605, rs, 1e-4);
        Assert.Equal(-0.1246, gs, 1e-4);
        Assert.Equal(-0.0182, bs, 1e-4);
    }

    [Fact]
    public void BT2020_AchromaticColours_StayAchromatic()
    {
        // For neutral grey input (R = G = B), output should also be
        // R = G = B because both spaces are normalised to D65 white.
        for (double v = 0.1; v < 0.9; v += 0.2)
        {
            VipsCicp2scRGB.ApplyPrimariesMatrix(VipsCicpPrimaries.BT2020,
                r: v, g: v, b: v, out double rs, out double gs, out double bs);
            // BT.2020 → BT.709 row sums are 1 by construction.
            Assert.Equal(v, rs, 3e-3);
            Assert.Equal(v, gs, 3e-3);
            Assert.Equal(v, bs, 3e-3);
        }
    }

    // ---------- End-to-end op tests ----------

    [Fact]
    public void Op_PQ_BT2020_8Bit_ProducesScRGB()
    {
        // A 1×1 UChar input at half-PQ (~128/255 ≈ 0.502) + BT.2020
        // primaries decodes to a scRGB triple near absolute SDR white
        // brightness (1.0) with neutral chroma.
        var src = new VipsImage
        {
            Width = 1, Height = 1, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                var addr = reg.GetAddress(0, 0);
                addr[0] = 128; addr[1] = 128; addr[2] = 128;
                return 0;
            }
        };
        var scRgb = VipsImageOps.Cicp2scRGB(src, VipsCicpPrimaries.BT2020, VipsCicpTransfer.PQ);
        Assert.Equal(VipsBandFormat.Float, scRgb.BandFormat);

        using var reg = new VipsRegion(scRgb);
        reg.Prepare(new VipsRect(0, 0, 1, 1));
        var addr = reg.GetAddress(0, 0);
        float r = BinaryPrimitives.ReadSingleLittleEndian(addr.Slice(0, 4));
        float g = BinaryPrimitives.ReadSingleLittleEndian(addr.Slice(4, 4));
        float b = BinaryPrimitives.ReadSingleLittleEndian(addr.Slice(8, 4));

        // Achromatic neutral grey should stay neutral after BT.2020 → sRGB.
        Assert.Equal(r, g, 1e-3);
        Assert.Equal(g, b, 1e-3);
        // PQ at code 128/255 ≈ 0.502 yields ≈ 0.95 (close to SDR diffuse white).
        Assert.InRange(r, 0.5f, 1.5f);
    }

    [Fact]
    public void Op_HLG_Half_GivesBreakpointValue()
    {
        // 1×1 UChar input where R = 128 (≈ 0.502) — picks up the lower
        // HLG segment (E' ≤ 0.5). Output should be roughly 0.084 — but
        // 128/255 ≈ 0.502, just barely past the breakpoint, so the
        // upper-segment formula kicks in. Either way, the value is small.
        var src = new VipsImage
        {
            Width = 1, Height = 1, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                var addr = reg.GetAddress(0, 0);
                addr[0] = 128; addr[1] = 128; addr[2] = 128;
                return 0;
            }
        };
        var scRgb = VipsImageOps.Cicp2scRGB(src, VipsCicpPrimaries.BT709, VipsCicpTransfer.HLG);
        using var reg = new VipsRegion(scRgb);
        reg.Prepare(new VipsRect(0, 0, 1, 1));
        var addr = reg.GetAddress(0, 0);
        float r = BinaryPrimitives.ReadSingleLittleEndian(addr.Slice(0, 4));
        // BT.709 primaries (= identity), so output equals scene-linear HLG decode.
        Assert.InRange(r, 0.05f, 0.15f);
    }

    [Fact]
    public void NonRGB_Or_NonStandardFormat_Rejected()
    {
        var grey = new VipsImage
        {
            Width = 4, Height = 4, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => 0,
        };
        Assert.Throws<Exception>(() => VipsImageOps.Cicp2scRGB(grey));
    }
}
