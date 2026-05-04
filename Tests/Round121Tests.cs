using System;
using System.Buffers.Binary;
using CosmoImage.Operations.Color;
using CosmoImage.Operations.Metadata;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 121 — ICC LUT-based profile support. The new
/// <see cref="VipsIccLutCmm"/> handles lut16Type ("mft2") tags via
/// the input-curves → matrix → 3D CLUT (trilinear) → output-curves
/// pipeline. Tests construct synthetic mft2 fixtures so the
/// pipeline can be validated end-to-end without depending on
/// specific real-world profiles.
/// </summary>
public class Round121Tests
{
    /// <summary>
    /// Build a synthetic mft2 tag payload.
    /// <paramref name="curveLen"/> controls the input/output table
    /// length; <paramref name="grid"/> is the per-axis CLUT grid size.
    /// All curves and the CLUT are filled in by the caller-supplied
    /// generator functions.
    /// </summary>
    private static byte[] BuildMft2(int inCh, int outCh, int grid, int curveLen,
        double[,] matrix,
        Func<int, int, ushort> inputCurve,    // (channel, idx) → 0..65535
        Func<int[], int, ushort> clutEntry,   // (gridIdx[], outChannel) → 0..65535
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
        // Matrix as 9 s15Fixed16 values.
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
    /// Wrap an mft2 payload in a minimal valid ICC profile (just a
    /// header + A2B0 + B2A0 tag table). The CMM only inspects the
    /// tag table, so the header fields can stay default.
    /// </summary>
    private static byte[] BuildSyntheticProfile(byte[] a2b0, byte[] b2a0)
    {
        var prof = new VipsIccProfile();
        prof.SetTagData("A2B0", a2b0);
        prof.SetTagData("B2A0", b2a0);
        return prof.ToBytes();
    }

    // ---- mft2 reader sanity ----

    [Fact]
    public void IccProfile_GetTagMft2_ParsesIdentity()
    {
        var identityMatrix = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        var mft2Bytes = BuildMft2(
            inCh: 3, outCh: 3, grid: 5, curveLen: 256,
            identityMatrix,
            inputCurve: (c, k) => (ushort)(k * 65535 / 255),
            clutEntry: (idx, c) => (ushort)(idx[c] * 65535 / 4),  // identity-ish
            outputCurve: (c, k) => (ushort)(k * 65535 / 255));
        var profile = new VipsIccProfile();
        profile.SetTagData("A2B0", mft2Bytes);
        var mft2 = profile.GetTagMft2("A2B0");
        Assert.NotNull(mft2);
        Assert.Equal(3, mft2!.InputChannels);
        Assert.Equal(3, mft2.OutputChannels);
        Assert.Equal(5, mft2.GridSize);
        Assert.Equal(256, mft2.InputTables[0].Length);
        Assert.Equal(256, mft2.OutputTables[0].Length);
        Assert.Equal(5 * 5 * 5 * 3, mft2.Clut.Length);
        Assert.Equal(1.0, mft2.Matrix[0, 0]);
        Assert.Equal(0.0, mft2.Matrix[0, 1]);
        Assert.Equal(1.0, mft2.Matrix[2, 2]);
    }

    [Fact]
    public void IccProfile_GetTagMft2_RejectsBadSignature()
    {
        var profile = new VipsIccProfile();
        var bogus = new byte[60];
        bogus[0] = (byte)'c'; bogus[1] = (byte)'u'; bogus[2] = (byte)'r'; bogus[3] = (byte)'v';
        profile.SetTagData("A2B0", bogus);
        Assert.Null(profile.GetTagMft2("A2B0"));
    }

    [Fact]
    public void IccProfile_GetTagMft2_ReturnsNullForMissingTag()
    {
        var profile = new VipsIccProfile();
        Assert.Null(profile.GetTagMft2("A2B0"));
    }

    // ---- VipsIccLutCmm pipeline ----

    /// <summary>
    /// Build an mft2 that maps device input → "halved" output: each
    /// channel of the device input lands as half of itself in the PCS.
    /// Pairing two of these (src + dst) does halve-then-double = identity
    /// for any input that survives the round-trip cleanly (no clipping).
    /// </summary>
    private static byte[] BuildHalveMft2()
    {
        var identity = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        return BuildMft2(
            inCh: 3, outCh: 3, grid: 17, curveLen: 256,
            identity,
            inputCurve: (c, k) => (ushort)(k * 65535 / 255),       // identity
            clutEntry: (idx, c) =>
            {
                // CLUT halves the input value: out = in / 2, where each
                // grid axis spans [0, 16] → [0, 1] PCS.
                double v = idx[c] / 16.0;
                return (ushort)Math.Round(v * 0.5 * 65535);
            },
            outputCurve: (c, k) => (ushort)(k * 65535 / 255));     // identity
    }

    /// <summary>
    /// Build an mft2 that doubles the input: out = clamp(in * 2).
    /// </summary>
    private static byte[] BuildDoubleMft2()
    {
        var identity = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        return BuildMft2(
            inCh: 3, outCh: 3, grid: 17, curveLen: 256,
            identity,
            inputCurve: (c, k) => (ushort)(k * 65535 / 255),
            clutEntry: (idx, c) =>
            {
                double v = idx[c] / 16.0;
                double doubled = Math.Min(1.0, v * 2.0);
                return (ushort)Math.Round(doubled * 65535);
            },
            outputCurve: (c, k) => (ushort)(k * 65535 / 255));
    }

    [Fact]
    public void LutCmm_HalveThenDouble_RoundTripsForUnclippedRange()
    {
        // src halves the values; dst doubles them. For inputs in [0..127],
        // the half-then-double round trip stays well within range.
        var halve = BuildHalveMft2();
        var doubl = BuildDoubleMft2();
        var srcProfile = BuildSyntheticProfile(halve, halve);
        var dstProfile = BuildSyntheticProfile(doubl, doubl);

        var src = VipsIccProfile.TryParse(srcProfile)!;
        var dst = VipsIccProfile.TryParse(dstProfile)!;
        var cmm = VipsIccLutCmm.TryBuild(src, dst);
        Assert.NotNull(cmm);

        // Test with values in the safe range (where halve + double is loss-bounded).
        var input = new byte[16 * 3];
        for (int i = 0; i < 16; i++)
        {
            input[i * 3] = (byte)(i * 7);
            input[i * 3 + 1] = (byte)(i * 4);
            input[i * 3 + 2] = (byte)(i * 5);
        }
        var output = new byte[input.Length];
        cmm!.Apply(input, 0, output, 0, 16, bands: 3);

        // Quantization through 256-entry curves and 17-grid CLUT is
        // bounded; we expect at most ~2-3 bytes of drift.
        for (int i = 0; i < input.Length; i++)
        {
            int delta = Math.Abs(input[i] - output[i]);
            Assert.True(delta <= 4, $"byte {i}: in={input[i]} out={output[i]} delta={delta}");
        }
    }

    [Fact]
    public void LutCmm_RgbaInput_AlphaPasses()
    {
        var halve = BuildHalveMft2();
        var doubl = BuildDoubleMft2();
        var src = VipsIccProfile.TryParse(BuildSyntheticProfile(halve, halve))!;
        var dst = VipsIccProfile.TryParse(BuildSyntheticProfile(doubl, doubl))!;
        var cmm = VipsIccLutCmm.TryBuild(src, dst)!;

        var input = new byte[8 * 4];
        for (int i = 0; i < 8; i++)
        {
            input[i * 4 + 0] = (byte)(i * 13);
            input[i * 4 + 1] = (byte)(i * 17);
            input[i * 4 + 2] = (byte)(i * 7);
            input[i * 4 + 3] = (byte)(0x80 + i);
        }
        var output = new byte[input.Length];
        cmm.Apply(input, 0, output, 0, 8, bands: 4);
        for (int i = 0; i < 8; i++)
            Assert.Equal(input[i * 4 + 3], output[i * 4 + 3]);
    }

    [Fact]
    public void LutCmm_TryBuildReturnsNullWhenTagsMissing()
    {
        // Profiles without A2B0 / B2A0 → null → caller falls back.
        var empty = new VipsIccProfile();
        Assert.Null(VipsIccLutCmm.TryBuild(empty, empty));
    }

    [Fact]
    public void LutCmm_TryBuildReturnsNullForChannelMismatch()
    {
        // 4-input profile (e.g., CMYK) — out of scope for this round.
        var identity = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        var fourCh = BuildMft2(
            inCh: 4, outCh: 3, grid: 5, curveLen: 256,
            identity,
            inputCurve: (c, k) => (ushort)k,
            clutEntry: (idx, c) => 0,
            outputCurve: (c, k) => (ushort)k);
        var threeChIdent = BuildHalveMft2();
        var src = VipsIccProfile.TryParse(BuildSyntheticProfile(fourCh, fourCh))!;
        var dst = VipsIccProfile.TryParse(BuildSyntheticProfile(threeChIdent, threeChIdent))!;
        Assert.Null(VipsIccLutCmm.TryBuild(src, dst));
    }
}
