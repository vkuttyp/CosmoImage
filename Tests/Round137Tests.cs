using System;
using System.Buffers.Binary;
using CosmoImage.Operations.Color;
using CosmoImage.Operations.Metadata;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 137 — ICC black-point compensation. Linearly scales values
/// in PCS XYZ between source A2B and destination B2A so that the
/// source profile's <c>bkpt</c> maps to the destination profile's
/// <c>bkpt</c>. Off by default; opt in via
/// <see cref="VipsIccTransform.BlackPointCompensation"/>. No-op when
/// both BPs are zero (matrix profiles like sRGB).
/// </summary>
public class Round137Tests
{
    private static byte[] BuildMft2(int inCh, int outCh, int grid, int curveLen,
        double[,] matrix,
        Func<int, int, ushort> inputCurve,
        Func<int[], int, ushort> clutEntry,
        Func<int, int, ushort> outputCurve)
    {
        int n = curveLen;
        int m = curveLen;
        int clutEntries = 1;
        for (int i = 0; i < inCh; i++) clutEntries *= grid;

        int size = 52 + 2 * inCh * n + 2 * clutEntries * outCh + 2 * outCh * m;
        var data = new byte[size];

        data[0] = (byte)'m'; data[1] = (byte)'f'; data[2] = (byte)'t'; data[3] = (byte)'2';
        data[8] = (byte)inCh;
        data[9] = (byte)outCh;
        data[10] = (byte)grid;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                WriteS15Fixed16(data, 12 + (i * 3 + j) * 4, matrix[i, j]);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(48, 2), (ushort)n);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(50, 2), (ushort)m);

        int p = 52;
        for (int c = 0; c < inCh; c++)
            for (int k = 0; k < n; k++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(p, 2), inputCurve(c, k));
                p += 2;
            }

        var idx = new int[inCh];
        WriteClutRecursive(data, ref p, idx, 0, inCh, outCh, grid, clutEntry);

        for (int c = 0; c < outCh; c++)
            for (int k = 0; k < m; k++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(p, 2), outputCurve(c, k));
                p += 2;
            }

        return data;
    }

    private static void WriteClutRecursive(byte[] data, ref int p, int[] idx, int dim,
        int inCh, int outCh, int grid, Func<int[], int, ushort> entry)
    {
        if (dim == inCh)
        {
            for (int c = 0; c < outCh; c++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(p, 2), entry(idx, c));
                p += 2;
            }
            return;
        }
        for (int i = 0; i < grid; i++)
        {
            idx[dim] = i;
            WriteClutRecursive(data, ref p, idx, dim + 1, inCh, outCh, grid, entry);
        }
    }

    private static void WriteS15Fixed16(byte[] buf, int off, double v)
    {
        int raw = (int)Math.Round(v * 65536.0);
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(off, 4), raw);
    }

    /// <summary>
    /// Identity-ish RGB-to-RGB LUT: each device value lands as the same
    /// value in PCS-encoded XYZ. With the same LUT used for both A2B
    /// and B2A, src + dst pair is a perfect round-trip absent BPC.
    /// </summary>
    private static byte[] BuildIdentityMft2()
    {
        var identity = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        return BuildMft2(
            inCh: 3, outCh: 3, grid: 17, curveLen: 256,
            identity,
            inputCurve: (c, k) => (ushort)(k * 65535 / 255),
            clutEntry: (idx, c) => (ushort)Math.Round(idx[c] / 16.0 * 65535),
            outputCurve: (c, k) => (ushort)(k * 65535 / 255));
    }

    private static byte[] BuildProfileWithBp(byte[] a2b0, byte[] b2a0, VipsColorXyz? bkpt)
    {
        var prof = new VipsIccProfile();
        prof.SetTagData("A2B0", a2b0);
        prof.SetTagData("B2A0", b2a0);
        if (bkpt.HasValue) prof.BlackPoint = bkpt.Value;
        return prof.ToBytes();
    }

    [Fact]
    public void LutCmm_NoBpc_BothBpsZero_RoundTripsIdentity()
    {
        // Sanity baseline: identity LUTs both ways with zero black points
        // give an effective identity transform. BPC flag has no effect.
        var ident = BuildIdentityMft2();
        var srcProf = BuildProfileWithBp(ident, ident, new VipsColorXyz(0, 0, 0));
        var dstProf = BuildProfileWithBp(ident, ident, new VipsColorXyz(0, 0, 0));
        var src = VipsIccProfile.TryParse(srcProf)!;
        var dst = VipsIccProfile.TryParse(dstProf)!;

        var withBpc = VipsIccLutCmm.TryBuild(src, dst,
            VipsIccRenderingIntent.Perceptual, blackPointCompensation: true)!;
        var input = new byte[] { 0, 0, 0, 64, 64, 64, 128, 128, 128, 200, 200, 200 };
        var output = new byte[input.Length];
        withBpc.Apply(input, 0, output, 0, 4, bands: 3);
        for (int i = 0; i < input.Length; i++)
            Assert.True(Math.Abs(input[i] - output[i]) <= 2, $"byte {i}: {input[i]} → {output[i]}");
    }

    [Fact]
    public void LutCmm_Bpc_LiftsShadowsWhenSrcBpNonZero()
    {
        // Source black at PCS Y ≈ 0.05 (a printable black, not perfect);
        // destination perfect black. With BPC ON, source-black-equivalent
        // device input should map closer to destination black after
        // round-trip. Without BPC, the offset isn't compensated and
        // shadow values just pass through unchanged.
        var ident = BuildIdentityMft2();
        var srcProf = BuildProfileWithBp(ident, ident, new VipsColorXyz(0.05, 0.05, 0.04));
        var dstProf = BuildProfileWithBp(ident, ident, new VipsColorXyz(0, 0, 0));
        var src = VipsIccProfile.TryParse(srcProf)!;
        var dst = VipsIccProfile.TryParse(dstProf)!;

        var noBpc = VipsIccLutCmm.TryBuild(src, dst,
            VipsIccRenderingIntent.Perceptual, blackPointCompensation: false)!;
        var withBpc = VipsIccLutCmm.TryBuild(src, dst,
            VipsIccRenderingIntent.Perceptual, blackPointCompensation: true)!;

        // Shadow input: 32 / 255 ≈ 0.125. With identity LUTs and zero src
        // BP, output ≈ input. With BPC pulling the source-black
        // contribution down toward zero, the output should drop below the
        // baseline.
        var input = new byte[] { 32, 32, 32 };
        var noBpcOut = new byte[3];
        var withBpcOut = new byte[3];
        noBpc.Apply(input, 0, noBpcOut, 0, 1, bands: 3);
        withBpc.Apply(input, 0, withBpcOut, 0, 1, bands: 3);

        // No-BPC: shadow value passes through.
        Assert.True(Math.Abs(noBpcOut[1] - 32) <= 3, $"no-bpc Y delta {noBpcOut[1]}");
        // With BPC: the source-black offset is removed → output is darker.
        Assert.True(withBpcOut[1] < noBpcOut[1] - 2,
            $"BPC should darken shadows: noBpc={noBpcOut[1]} withBpc={withBpcOut[1]}");
    }

    [Fact]
    public void LutCmm_Bpc_PreservesWhitePoint()
    {
        // White (255, 255, 255) device → white-equivalent PCS regardless
        // of BPC: the BPC scaling fixes (D50, D50) → (D50, D50). Any
        // distortion at white means BPC is mis-scaling.
        var ident = BuildIdentityMft2();
        var srcProf = BuildProfileWithBp(ident, ident, new VipsColorXyz(0.05, 0.05, 0.04));
        var dstProf = BuildProfileWithBp(ident, ident, new VipsColorXyz(0, 0, 0));
        var src = VipsIccProfile.TryParse(srcProf)!;
        var dst = VipsIccProfile.TryParse(dstProf)!;

        var withBpc = VipsIccLutCmm.TryBuild(src, dst,
            VipsIccRenderingIntent.Perceptual, blackPointCompensation: true)!;

        var input = new byte[] { 255, 255, 255 };
        var output = new byte[3];
        withBpc.Apply(input, 0, output, 0, 1, bands: 3);
        // Allow small quantization drift.
        Assert.True(output[0] >= 250, $"R white: got {output[0]}");
        Assert.True(output[1] >= 250, $"G white: got {output[1]}");
        Assert.True(output[2] >= 250, $"B white: got {output[2]}");
    }

    [Fact]
    public void IccTransform_BlackPointCompensationFlag_ChangesCacheKey()
    {
        // Confirm the cache key incorporates the BPC flag — two
        // otherwise-identical transforms with different BPC should not
        // share a cached result.
        var ident = BuildIdentityMft2();
        var profBytes = BuildProfileWithBp(ident, ident, new VipsColorXyz(0.05, 0.05, 0.04));

        var img = new VipsImage { Width = 4, Height = 4, Bands = 3, BandFormat = VipsBandFormat.UChar };
        var t1 = new VipsIccTransform
        {
            In = img,
            InputProfile = profBytes,
            OutputProfile = profBytes,
            BlackPointCompensation = false,
        };
        var t2 = new VipsIccTransform
        {
            In = img,
            InputProfile = profBytes,
            OutputProfile = profBytes,
            BlackPointCompensation = true,
        };
        Assert.NotEqual(t1.GetCacheKey(), t2.GetCacheKey());
    }
}
