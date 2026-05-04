using System;
using System.Buffers.Binary;
using CosmoImage.Operations.Color;
using CosmoImage.Operations.Metadata;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 124 — mft1 (lut8Type) support. Older 8-bit LUT format
/// with fixed 256-entry curves. Maps internally to the same
/// IccMft2 representation that mft2 uses, so the existing
/// Mft2Transform pipeline handles both.
/// </summary>
public class Round124Tests
{
    /// <summary>
    /// Build a synthetic mft1 tag with given input/output curves and
    /// CLUT generator. Curves are always 256 entries of 8-bit data;
    /// CLUT entries are 8-bit per spec.
    /// </summary>
    private static byte[] BuildMft1(int inCh, int outCh, int grid,
        double[,] matrix,
        Func<int, int, byte> inputCurve,
        Func<int[], int, byte> clutEntry,
        Func<int, int, byte> outputCurve)
    {
        const int n = 256;
        int clutEntries = 1;
        for (int i = 0; i < inCh; i++) clutEntries *= grid;
        int size = 48 + inCh * n + clutEntries * outCh + outCh * n;
        var data = new byte[size];

        data[0] = (byte)'m'; data[1] = (byte)'f'; data[2] = (byte)'t'; data[3] = (byte)'1';
        data[8] = (byte)inCh; data[9] = (byte)outCh; data[10] = (byte)grid;

        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                WriteS15Fixed16(data, 12 + (i * 3 + j) * 4, matrix[i, j]);

        int p = 48;
        for (int c = 0; c < inCh; c++)
            for (int k = 0; k < n; k++)
                data[p++] = inputCurve(c, k);

        var idx = new int[inCh];
        WriteClutRecursive(data, ref p, idx, 0, inCh, outCh, grid, clutEntry);

        for (int c = 0; c < outCh; c++)
            for (int k = 0; k < n; k++)
                data[p++] = outputCurve(c, k);

        return data;
    }

    private static void WriteClutRecursive(byte[] data, ref int p, int[] idx, int dim,
        int inCh, int outCh, int grid, Func<int[], int, byte> entry)
    {
        if (dim == inCh)
        {
            for (int c = 0; c < outCh; c++) data[p++] = entry(idx, c);
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

    private static byte[] BuildSyntheticProfile(byte[] a2b0, byte[] b2a0)
    {
        var prof = new VipsIccProfile();
        prof.SetTagData("A2B0", a2b0);
        prof.SetTagData("B2A0", b2a0);
        return prof.ToBytes();
    }

    [Fact]
    public void IccProfile_GetTagMft1_ParsesAndScales()
    {
        var identity = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        var bytes = BuildMft1(3, 3, 5, identity,
            inputCurve: (c, k) => (byte)k,
            clutEntry: (idx, c) => (byte)(idx[c] * 64),  // 0..192 across the grid
            outputCurve: (c, k) => (byte)k);
        var prof = new VipsIccProfile();
        prof.SetTagData("A2B0", bytes);
        var parsed = prof.GetTagMft1("A2B0");
        Assert.NotNull(parsed);
        Assert.Equal(3, parsed!.InputChannels);
        Assert.Equal(3, parsed.OutputChannels);
        Assert.Equal(5, parsed.GridSize);
        // 8-bit input curve entry 0 → 0; 255 → 255*257 = 65535 (full range).
        Assert.Equal(0, parsed.InputTables[0][0]);
        Assert.Equal(65535, parsed.InputTables[0][255]);
        // CLUT entry 64 (8-bit) → 64 * 257 = 16448.
        Assert.Contains((ushort)16448, parsed.Clut);
    }

    [Fact]
    public void LutCmm_AcceptsMft1Profile()
    {
        // Identity-ish mft1: linear curves, identity-ish CLUT.
        var identity = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        const int g = 5;
        var bytes = BuildMft1(3, 3, g, identity,
            inputCurve: (c, k) => (byte)k,
            clutEntry: (idx, c) => (byte)(idx[c] * 255 / (g - 1)),
            outputCurve: (c, k) => (byte)k);

        var src = VipsIccProfile.TryParse(BuildSyntheticProfile(bytes, bytes))!;
        var dst = VipsIccProfile.TryParse(BuildSyntheticProfile(bytes, bytes))!;
        var cmm = VipsIccLutCmm.TryBuild(src, dst);
        Assert.NotNull(cmm);
        Assert.Equal(3, cmm!.SrcChannels);
        Assert.Equal(3, cmm.DstChannels);

        // Identity round trip on a small set of pixels.
        var input = new byte[]
        {
            64, 128, 192,
            255, 0, 128,
            10, 200, 50,
        };
        var output = new byte[input.Length];
        cmm.Apply(input, 0, output, 0, 3, bands: 3);
        for (int i = 0; i < input.Length; i++)
        {
            int delta = Math.Abs(input[i] - output[i]);
            Assert.True(delta <= 4, $"byte {i}: in={input[i]} out={output[i]} delta={delta}");
        }
    }
}
