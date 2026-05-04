using System;
using System.Buffers.Binary;
using CosmoImage.Operations.Color;
using CosmoImage.Operations.Metadata;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 123 — n-linear CLUT (1..4 input dims) and mixed source /
/// destination band counts. Lets the LUT CMM handle RGB → CMYK and
/// CMYK → RGB transforms via the same pipeline.
/// </summary>
public class Round123Tests
{
    private static byte[] BuildLutAB(bool isAtoB, int inCh, int outCh,
        ushort[][]? aCurves = null, ushort[][]? bCurves = null,
        ushort[][]? mCurves = null, double[,]? matrix = null,
        (int[] grids, ushort[] data)? clut = null)
    {
        var components = new System.Collections.Generic.List<(int OffsetField, byte[] Bytes)>();
        if (bCurves != null) components.Add((12, BuildCurveStream(bCurves)));
        if (matrix != null)  components.Add((16, BuildMatrixStream(matrix)));
        if (mCurves != null) components.Add((20, BuildCurveStream(mCurves)));
        if (clut != null)    components.Add((24, BuildClutStream(clut.Value.grids, clut.Value.data, outCh)));
        if (aCurves != null) components.Add((28, BuildCurveStream(aCurves)));

        int total = 32;
        var offsets = new int[components.Count];
        for (int i = 0; i < components.Count; i++)
        {
            offsets[i] = total;
            total += components[i].Bytes.Length;
            total = (total + 3) & ~3;
        }
        var data = new byte[total];
        string sig = isAtoB ? "mAB " : "mBA ";
        for (int i = 0; i < 4; i++) data[i] = (byte)sig[i];
        data[8] = (byte)inCh;
        data[9] = (byte)outCh;
        for (int i = 0; i < components.Count; i++)
        {
            int field = components[i].OffsetField;
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(field, 4), (uint)offsets[i]);
            Array.Copy(components[i].Bytes, 0, data, offsets[i], components[i].Bytes.Length);
        }
        return data;
    }

    private static byte[] BuildCurveStream(ushort[][] curves)
    {
        int total = 0;
        foreach (var c in curves) total += ((12 + c.Length * 2) + 3) & ~3;
        var data = new byte[total];
        int p = 0;
        foreach (var c in curves)
        {
            data[p] = (byte)'c'; data[p + 1] = (byte)'u'; data[p + 2] = (byte)'r'; data[p + 3] = (byte)'v';
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(p + 8, 4), (uint)c.Length);
            for (int i = 0; i < c.Length; i++)
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(p + 12 + i * 2, 2), c[i]);
            p += ((12 + c.Length * 2) + 3) & ~3;
        }
        return data;
    }

    private static byte[] BuildMatrixStream(double[,] m)
    {
        var data = new byte[12 * 4];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                WriteS15Fixed16(data, (i * 3 + j) * 4, m[i, j]);
        for (int i = 0; i < 3; i++)
            WriteS15Fixed16(data, (9 + i) * 4, m[i, 3]);
        return data;
    }

    private static byte[] BuildClutStream(int[] grids, ushort[] entries, int outCh)
    {
        var data = new byte[20 + entries.Length * 2];
        for (int i = 0; i < grids.Length && i < 16; i++) data[i] = (byte)grids[i];
        data[16] = 2;
        for (int i = 0; i < entries.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(20 + i * 2, 2), entries[i]);
        return data;
    }

    private static void WriteS15Fixed16(byte[] buf, int off, double v)
    {
        int raw = (int)Math.Round(v * 65536.0);
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(off, 4), raw);
    }

    private static byte[] BuildSyntheticProfile(byte[] a2b0, byte[] b2a0)
    {
        var prof = new VipsIccProfile();
        prof.SetTagData("A2B0", a2b0);
        prof.SetTagData("B2A0", b2a0);
        return prof.ToBytes();
    }

    /// <summary>
    /// Build a CMYK source profile whose A2B0 is mAB with a 4D CLUT
    /// implementing the standard inversion CMYK → RGB:
    ///   R = (1 - C) * (1 - K)
    ///   G = (1 - M) * (1 - K)
    ///   B = (1 - Y) * (1 - K)
    /// PCS values land in [0, 1] (interpreted as XYZ pseudo).
    /// </summary>
    private static byte[] BuildCmykSourceProfile()
    {
        // Identity curves (no transform).
        var idCurve = new ushort[256];
        for (int i = 0; i < 256; i++) idCurve[i] = (ushort)(i * 65535 / 255);

        // 4D CLUT (5×5×5×5) with the inversion formula at each grid point.
        const int g = 5;
        var clutData = new ushort[g * g * g * g * 3];
        int idx = 0;
        for (int c = 0; c < g; c++)
            for (int m = 0; m < g; m++)
                for (int y = 0; y < g; y++)
                    for (int k = 0; k < g; k++)
                    {
                        double cv = c / (double)(g - 1);
                        double mv = m / (double)(g - 1);
                        double yv = y / (double)(g - 1);
                        double kv = k / (double)(g - 1);
                        double r = (1 - cv) * (1 - kv);
                        double gn = (1 - mv) * (1 - kv);
                        double b = (1 - yv) * (1 - kv);
                        clutData[idx++] = (ushort)Math.Round(r * 65535);
                        clutData[idx++] = (ushort)Math.Round(gn * 65535);
                        clutData[idx++] = (ushort)Math.Round(b * 65535);
                    }

        // Identity matrix + identity B curves.
        var identity = new double[3, 4] { { 1, 0, 0, 0 }, { 0, 1, 0, 0 }, { 0, 0, 1, 0 } };
        var bCurves = new[] { idCurve, idCurve, idCurve };
        // M curves are 3 channels (the PCS-side curves).
        var mab = BuildLutAB(isAtoB: true, inCh: 4, outCh: 3,
            aCurves: new[] { idCurve, idCurve, idCurve, idCurve },
            bCurves: bCurves,
            matrix: identity,
            clut: (new[] { g, g, g, g }, clutData));
        return BuildSyntheticProfile(mab, mab);
    }

    /// <summary>
    /// Build an RGB destination profile whose B2A0 is mBA with an
    /// identity 3D LUT (PCS RGB → device RGB unchanged).
    /// </summary>
    private static byte[] BuildIdentityRgbDest()
    {
        var idCurve = new ushort[256];
        for (int i = 0; i < 256; i++) idCurve[i] = (ushort)(i * 65535 / 255);

        const int g = 5;
        var clutData = new ushort[g * g * g * 3];
        int idx = 0;
        for (int a = 0; a < g; a++)
            for (int b = 0; b < g; b++)
                for (int c = 0; c < g; c++)
                {
                    clutData[idx++] = (ushort)(a * 65535 / (g - 1));
                    clutData[idx++] = (ushort)(b * 65535 / (g - 1));
                    clutData[idx++] = (ushort)(c * 65535 / (g - 1));
                }

        var identity = new double[3, 4] { { 1, 0, 0, 0 }, { 0, 1, 0, 0 }, { 0, 0, 1, 0 } };
        var mba = BuildLutAB(isAtoB: false, inCh: 3, outCh: 3,
            aCurves: new[] { idCurve, idCurve, idCurve },
            bCurves: new[] { idCurve, idCurve, idCurve },
            matrix: identity,
            clut: (new[] { g, g, g }, clutData));
        return BuildSyntheticProfile(mba, mba);
    }

    [Fact]
    public void LutCmm_CmykToRgb_TryBuildSucceeds()
    {
        var src = VipsIccProfile.TryParse(BuildCmykSourceProfile())!;
        var dst = VipsIccProfile.TryParse(BuildIdentityRgbDest())!;
        var cmm = VipsIccLutCmm.TryBuild(src, dst);
        Assert.NotNull(cmm);
        Assert.Equal(4, cmm!.SrcChannels);
        Assert.Equal(3, cmm.DstChannels);
    }

    [Fact]
    public void LutCmm_CmykToRgb_AppliesInversion()
    {
        var src = VipsIccProfile.TryParse(BuildCmykSourceProfile())!;
        var dst = VipsIccProfile.TryParse(BuildIdentityRgbDest())!;
        var cmm = VipsIccLutCmm.TryBuild(src, dst)!;

        // CMYK input pixels.
        var cmyk = new byte[]
        {
            0,   0,   0,   0,   // pure white in CMYK → RGB white
            255, 0,   0,   0,   // pure cyan → RGB has zero R, full G/B
            0,   255, 0,   0,   // pure magenta → zero G, full R/B
            0,   0,   255, 0,   // pure yellow → zero B, full R/G
            0,   0,   0,   255, // pure black (K only) → RGB black
            128, 128, 128, 0,   // 50% CMY, no K → mid-gray
        };
        var rgb = new byte[6 * 3];
        cmm.Apply(cmyk, 0, srcBands: 4, rgb, 0, dstBands: 3, count: 6);

        // White (C=M=Y=K=0): R = (1-0)*(1-0) = 1 → 255.
        Assert.True(rgb[0] >= 250); Assert.True(rgb[1] >= 250); Assert.True(rgb[2] >= 250);
        // Pure cyan (C=255): R = (1-1)*(1-0) = 0; G = (1-0)*(1-0) = 1; B = same.
        Assert.True(rgb[3] <= 5);  Assert.True(rgb[4] >= 250); Assert.True(rgb[5] >= 250);
        // Pure magenta (M=255): R = 1, G = 0, B = 1.
        Assert.True(rgb[6] >= 250); Assert.True(rgb[7] <= 5);  Assert.True(rgb[8] >= 250);
        // Pure yellow (Y=255): R = 1, G = 1, B = 0.
        Assert.True(rgb[9] >= 250); Assert.True(rgb[10] >= 250); Assert.True(rgb[11] <= 5);
        // Pure black (K=255): all components scaled by (1-1) = 0.
        Assert.True(rgb[12] <= 5); Assert.True(rgb[13] <= 5); Assert.True(rgb[14] <= 5);
        // 50% CMY, K=0: R = (1-0.5)*(1-0) = 0.5 → ~128, etc.
        Assert.InRange(rgb[15], 120, 135);
        Assert.InRange(rgb[16], 120, 135);
        Assert.InRange(rgb[17], 120, 135);
    }

    [Fact]
    public void LutCmm_CmykWithAlpha_PassesThrough()
    {
        // Source: CMYK + alpha (5 bands), dest: RGB + alpha (4 bands).
        var src = VipsIccProfile.TryParse(BuildCmykSourceProfile())!;
        var dst = VipsIccProfile.TryParse(BuildIdentityRgbDest())!;
        var cmm = VipsIccLutCmm.TryBuild(src, dst)!;

        var cmykA = new byte[]
        {
            0, 0, 0, 0, 0xC0,
            255, 0, 0, 0, 0x80,
        };
        var rgbA = new byte[2 * 4];
        cmm.Apply(cmykA, 0, srcBands: 5, rgbA, 0, dstBands: 4, count: 2);

        // Alpha pass-through.
        Assert.Equal(0xC0, rgbA[3]);
        Assert.Equal(0x80, rgbA[7]);
    }
}
