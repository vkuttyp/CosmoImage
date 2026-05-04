using System;
using CosmoImage.Core;
using CosmoImage.Operations.Color;
using CosmoImage.Operations.Metadata;
using ImageMagick;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 114 — pure-managed ICC color management for Matrix/TRC
/// profiles. Validates that the new CMM agrees with Magick's
/// LittleCMS-backed reference within reasonable tolerance, that
/// identity transforms are exact, and that round-trips converge.
/// </summary>
public class Round114Tests
{
    private static byte[] LoadProfile(IImageProfile p)
    {
        using var ms = new System.IO.MemoryStream();
        ms.Write(p.ToByteArray());
        return ms.ToArray();
    }

    private static readonly byte[] SrgbProfile = LoadProfile(ColorProfiles.SRGB);
    private static readonly byte[] AdobeRgbProfile = LoadProfile(ColorProfiles.AdobeRGB1998);

    private static byte[] BuildRgbPixels(int w, int h)
    {
        var px = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                px[o] = (byte)((x * 7) & 0xFF);
                px[o + 1] = (byte)((y * 11) & 0xFF);
                px[o + 2] = (byte)(((x + y) * 13) & 0xFF);
            }
        return px;
    }

    /// <summary>
    /// Run pixels through Magick's CMM and return the transformed bytes
    /// — used as ground truth for the pure CMM.
    /// </summary>
    private static byte[] MagickCmm(byte[] rgb, int w, int h, byte[] srcProfile, byte[] dstProfile)
    {
        var settings = new MagickReadSettings { Width = (uint)w, Height = (uint)h, Format = MagickFormat.Rgb, Depth = 8 };
        using var img = new MagickImage();
        img.Read(rgb, settings);
        img.SetProfile(new ColorProfile(srcProfile));
        img.SetProfile(new ColorProfile(dstProfile));
        return img.ToByteArray(MagickFormat.Rgb);
    }

    private static int MaxByteDelta(byte[] a, byte[] b)
    {
        int max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            if (d > max) max = d;
        }
        return max;
    }

    // ---- TryBuild ----

    [Fact]
    public void Cmm_BuildsForMatrixTrcProfilePair()
    {
        var src = VipsIccProfile.TryParse(SrgbProfile);
        var dst = VipsIccProfile.TryParse(AdobeRgbProfile);
        Assert.NotNull(src);
        Assert.NotNull(dst);
        var cmm = VipsIccCmm.TryBuild(src!, dst!);
        Assert.NotNull(cmm);
    }

    [Fact]
    public void Cmm_TryBuildReturnsNullForNullProfiles()
    {
        Assert.Null(VipsIccCmm.TryBuild(null!, null!));
    }

    // ---- Identity transform ----

    [Fact]
    public void Cmm_SrgbToSrgb_IsIdentityWithinOneByte()
    {
        // Identity transforms aren't bit-exact because of forward/inverse
        // LUT quantization, but should be within 1 byte everywhere.
        int w = 16, h = 8;
        var src = BuildRgbPixels(w, h);
        var srcP = VipsIccProfile.TryParse(SrgbProfile)!;
        var dstP = VipsIccProfile.TryParse(SrgbProfile)!;
        var cmm = VipsIccCmm.TryBuild(srcP, dstP)!;
        var dst = new byte[src.Length];
        cmm.Apply(src, 0, dst, 0, w * h, bands: 3);
        Assert.True(MaxByteDelta(src, dst) <= 1, $"identity max delta = {MaxByteDelta(src, dst)}");
    }

    // ---- Cross-profile transforms ----

    [Fact]
    public void Cmm_SrgbToAdobeRgb_AgreesWithMagick()
    {
        int w = 32, h = 16;
        var src = BuildRgbPixels(w, h);
        var srcP = VipsIccProfile.TryParse(SrgbProfile)!;
        var dstP = VipsIccProfile.TryParse(AdobeRgbProfile)!;
        var cmm = VipsIccCmm.TryBuild(srcP, dstP)!;
        var pure = new byte[src.Length];
        cmm.Apply(src, 0, pure, 0, w * h, bands: 3);

        var magick = MagickCmm(src, w, h, SrgbProfile, AdobeRgbProfile);
        // Allow a small delta — different CMMs differ slightly on the
        // exact rounding strategy and gamut handling.
        int delta = MaxByteDelta(pure, magick);
        Assert.True(delta <= 4, $"sRGB→AdobeRGB max delta = {delta}");
    }

    // ---- Round-trip ----

    [Fact]
    public void Cmm_SrgbAdobeRgbRoundTrip_ConvergesToOriginal()
    {
        int w = 16, h = 8;
        var src = BuildRgbPixels(w, h);
        var srcP = VipsIccProfile.TryParse(SrgbProfile)!;
        var midP = VipsIccProfile.TryParse(AdobeRgbProfile)!;
        var fwd = VipsIccCmm.TryBuild(srcP, midP)!;
        var rev = VipsIccCmm.TryBuild(midP, srcP)!;

        var mid = new byte[src.Length];
        fwd.Apply(src, 0, mid, 0, w * h, 3);
        var roundTripped = new byte[src.Length];
        rev.Apply(mid, 0, roundTripped, 0, w * h, 3);

        // 1-2 byte loss expected on the round trip due to quantization.
        Assert.True(MaxByteDelta(src, roundTripped) <= 3,
            $"round-trip max delta = {MaxByteDelta(src, roundTripped)}");
    }

    // ---- RGBA passthrough ----

    [Fact]
    public void Cmm_RgbaInput_AlphaPassesThrough()
    {
        int w = 8, h = 4;
        var src = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            src[i * 4] = (byte)(i * 7);
            src[i * 4 + 1] = (byte)(i * 11);
            src[i * 4 + 2] = (byte)(i * 13);
            src[i * 4 + 3] = (byte)(0x80 + i * 3);
        }

        var srcP = VipsIccProfile.TryParse(SrgbProfile)!;
        var dstP = VipsIccProfile.TryParse(AdobeRgbProfile)!;
        var cmm = VipsIccCmm.TryBuild(srcP, dstP)!;
        var dst = new byte[src.Length];
        cmm.Apply(src, 0, dst, 0, w * h, bands: 4);

        for (int i = 0; i < w * h; i++)
            Assert.Equal(src[i * 4 + 3], dst[i * 4 + 3]);
    }

    // ---- Parametric curve evaluator ----

    [Fact]
    public void IccProfile_ParaCurveEvaluator_EvaluatesSrgbCurve()
    {
        // The sRGB profile uses 'para' or 'curv' curves. Validate that
        // GetTagCurveEvaluator returns a function that maps 0→0 and 1→1
        // monotonically.
        var p = VipsIccProfile.TryParse(SrgbProfile)!;
        var ev = p.GetTagCurveEvaluator("rTRC");
        Assert.NotNull(ev);
        Assert.Equal(0.0, ev!(0.0), 3);
        Assert.Equal(1.0, ev(1.0), 3);
        // Monotonic.
        double prev = -0.1;
        for (int i = 0; i <= 100; i++)
        {
            double v = ev(i / 100.0);
            Assert.True(v >= prev - 1e-6, $"non-monotonic at i={i}");
            prev = v;
        }
    }
}
