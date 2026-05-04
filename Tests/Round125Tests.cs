using System;
using System.Buffers.Binary;
using CosmoImage.Operations.Color;
using CosmoImage.Operations.Metadata;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 125 — Lab↔XYZ PCS conversion. Real-world ICC pipelines
/// often have one profile using XYZ PCS (display profiles) and
/// another using Lab PCS (printer profiles). The CMM must insert
/// a conversion step between A2B and B2A when the two PCS encodings
/// disagree.
/// </summary>
public class Round125Tests
{
    private static byte[] BuildIdentityMft2(int inCh, int outCh, int grid)
    {
        int n = 256;
        int clutEntries = 1;
        for (int i = 0; i < inCh; i++) clutEntries *= grid;
        int size = 52 + 2 * inCh * n + 2 * clutEntries * outCh + 2 * outCh * n;
        var data = new byte[size];

        data[0] = (byte)'m'; data[1] = (byte)'f'; data[2] = (byte)'t'; data[3] = (byte)'2';
        data[8] = (byte)inCh; data[9] = (byte)outCh; data[10] = (byte)grid;
        // Identity matrix.
        for (int i = 0; i < 3; i++) WriteS15Fixed16(data, 12 + (i * 3 + i) * 4, 1.0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(48, 2), (ushort)n);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(50, 2), (ushort)n);

        int p = 52;
        // Identity input/output curves.
        for (int c = 0; c < inCh; c++)
            for (int k = 0; k < n; k++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(p, 2), (ushort)(k * 65535 / 255));
                p += 2;
            }
        // Identity CLUT — output equals normalized input.
        var idx = new int[inCh];
        WriteIdentityClut(data, ref p, idx, 0, inCh, outCh, grid);
        for (int c = 0; c < outCh; c++)
            for (int k = 0; k < n; k++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(p, 2), (ushort)(k * 65535 / 255));
                p += 2;
            }
        return data;
    }

    private static void WriteIdentityClut(byte[] data, ref int p, int[] idx, int dim,
        int inCh, int outCh, int grid)
    {
        if (dim == inCh)
        {
            for (int c = 0; c < outCh; c++)
            {
                ushort v = (ushort)(idx[c] * 65535 / (grid - 1));
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(p, 2), v);
                p += 2;
            }
            return;
        }
        for (int i = 0; i < grid; i++)
        {
            idx[dim] = i;
            WriteIdentityClut(data, ref p, idx, dim + 1, inCh, outCh, grid);
        }
    }

    private static void WriteS15Fixed16(byte[] buf, int off, double v)
    {
        int raw = (int)Math.Round(v * 65536.0);
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(off, 4), raw);
    }

    /// <summary>
    /// Build a synthetic profile that's an identity LUT in either
    /// XYZ or Lab PCS. The PCS choice is recorded in the profile
    /// header so VipsIccLutCmm.TryBuild picks it up.
    /// </summary>
    private static byte[] BuildIdentityProfile(VipsIccColorSpace pcs)
    {
        var prof = new VipsIccProfile { ConnectionColorSpace = pcs };
        var mft2 = BuildIdentityMft2(3, 3, 5);
        prof.SetTagData("A2B0", mft2);
        prof.SetTagData("B2A0", mft2);
        return prof.ToBytes();
    }

    [Fact]
    public void LutCmm_BuildsAcrossDifferentPcsTypes()
    {
        var srcXyz = VipsIccProfile.TryParse(BuildIdentityProfile(VipsIccColorSpace.Xyz))!;
        var dstLab = VipsIccProfile.TryParse(BuildIdentityProfile(VipsIccColorSpace.Lab))!;
        var cmm = VipsIccLutCmm.TryBuild(srcXyz, dstLab);
        Assert.NotNull(cmm);
    }

    [Fact]
    public void LutCmm_XyzToLabRoundTrip_StaysInGamut_WithModestInputs()
    {
        // Test inputs near "PCS white" (byte 128 ≈ normalized 0.5 ≈
        // absolute Y=1.0, the D50 reference). With identity profiles
        // these stay in gamut on both PCS sides so the round-trip
        // bounds are reasonable.
        var srcXyz = VipsIccProfile.TryParse(BuildIdentityProfile(VipsIccColorSpace.Xyz))!;
        var dstLab = VipsIccProfile.TryParse(BuildIdentityProfile(VipsIccColorSpace.Lab))!;
        var cmm = VipsIccLutCmm.TryBuild(srcXyz, dstLab)!;

        var srcLab = VipsIccProfile.TryParse(BuildIdentityProfile(VipsIccColorSpace.Lab))!;
        var dstXyz = VipsIccProfile.TryParse(BuildIdentityProfile(VipsIccColorSpace.Xyz))!;
        var cmmReverse = VipsIccLutCmm.TryBuild(srcLab, dstXyz)!;

        // Inputs around byte 128 keep absolute XYZ values around D50,
        // where the Lab nonlinearity is benign and round-trip drift
        // stays modest.
        var input = new byte[]
        {
            128, 128, 128,
            120, 130, 125,
            100, 110, 90,
        };
        var step1 = new byte[input.Length];
        cmm.Apply(input, 0, step1, 0, 3, bands: 3);
        var step2 = new byte[input.Length];
        cmmReverse.Apply(step1, 0, step2, 0, 3, bands: 3);

        for (int i = 0; i < input.Length; i++)
        {
            int delta = Math.Abs(input[i] - step2[i]);
            Assert.True(delta <= 8, $"byte {i}: in={input[i]} round-tripped={step2[i]} delta={delta}");
        }
    }

    [Fact]
    public void LutCmm_PcsConversionBypassed_WhenPcsMatches()
    {
        // When src.PCS == dst.PCS, the conversion step is bypassed and
        // round-trip with identity LUTs is bounded only by quantization.
        // Confirm this is significantly tighter than the cross-PCS case.
        var src = VipsIccProfile.TryParse(BuildIdentityProfile(VipsIccColorSpace.Lab))!;
        var dst = VipsIccProfile.TryParse(BuildIdentityProfile(VipsIccColorSpace.Lab))!;
        var cmm = VipsIccLutCmm.TryBuild(src, dst)!;

        var input = new byte[] { 200, 50, 175,  10, 240, 90 };
        var output = new byte[input.Length];
        cmm.Apply(input, 0, output, 0, 2, bands: 3);
        for (int i = 0; i < input.Length; i++)
        {
            int delta = Math.Abs(input[i] - output[i]);
            Assert.True(delta <= 4, $"same-PCS byte {i}: in={input[i]} out={output[i]} delta={delta}");
        }
    }

    [Fact]
    public void LutCmm_SamePcs_NoConversionInserted()
    {
        // Both profiles use XYZ PCS — conversion path is bypassed,
        // round-trip should be very tight (just LUT quantization).
        var src = VipsIccProfile.TryParse(BuildIdentityProfile(VipsIccColorSpace.Xyz))!;
        var dst = VipsIccProfile.TryParse(BuildIdentityProfile(VipsIccColorSpace.Xyz))!;
        var cmm = VipsIccLutCmm.TryBuild(src, dst)!;

        var input = new byte[] { 100, 150, 200,  50, 75, 25 };
        var output = new byte[input.Length];
        cmm.Apply(input, 0, output, 0, 2, bands: 3);

        for (int i = 0; i < input.Length; i++)
        {
            int delta = Math.Abs(input[i] - output[i]);
            Assert.True(delta <= 4, $"byte {i}: in={input[i]} out={output[i]} delta={delta}");
        }
    }
}
