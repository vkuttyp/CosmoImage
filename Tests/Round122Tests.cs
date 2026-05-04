using System;
using System.Buffers.Binary;
using CosmoImage.Operations.Color;
using CosmoImage.Operations.Metadata;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 122 — modern ICC v4 LUT tags ('mAB ' / 'mBA '). Builds
/// synthetic fixtures with various component combinations
/// (matrix-only, curves-only, CLUT-only, full pipeline) and
/// verifies the reader + apply pipeline round-trip correctly.
/// </summary>
public class Round122Tests
{
    /// <summary>
    /// Build an mAB or mBA tag with the requested optional components.
    /// Each non-null parameter slots into the appropriate offset; nulls
    /// produce offset=0 (component absent).
    /// </summary>
    private static byte[] BuildLutAB(bool isAtoB, int inCh, int outCh,
        ushort[][]? aCurves = null,    // length = isAtoB ? inCh : outCh
        ushort[][]? bCurves = null,    // length = isAtoB ? outCh : inCh
        ushort[][]? mCurves = null,    // length = 3 if present
        double[,]? matrix = null,      // 3×4 (3×3 + 3 offsets)
        (int[] grids, ushort[] data)? clut = null)
    {
        // Layout: 32-byte header, then components in order.
        var components = new System.Collections.Generic.List<(string Name, byte[] Bytes, int OffsetField)>();

        if (bCurves != null) components.Add(("B", BuildCurveStream(bCurves), 12));
        if (matrix != null)  components.Add(("M", BuildMatrixStream(matrix), 16));
        if (mCurves != null) components.Add(("Mc", BuildCurveStream(mCurves), 20));
        if (clut != null)    components.Add(("CL", BuildClutStream(clut.Value.grids, clut.Value.data, outCh), 24));
        if (aCurves != null) components.Add(("A", BuildCurveStream(aCurves), 28));

        // Compute offsets.
        int total = 32;
        var offsets = new int[components.Count];
        for (int i = 0; i < components.Count; i++)
        {
            offsets[i] = total;
            total += components[i].Bytes.Length;
            // 4-byte align between components.
            total = (total + 3) & ~3;
        }

        var data = new byte[total];
        // Signature.
        var sig = isAtoB ? "mAB " : "mBA ";
        for (int i = 0; i < 4; i++) data[i] = (byte)sig[i];
        data[8] = (byte)inCh;
        data[9] = (byte)outCh;
        // Offset fields default to 0 (absent).
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
        // Each curve is 'curv' (sig + reserved + count + entries), padded to 4.
        int total = 0;
        foreach (var c in curves)
        {
            int len = 12 + c.Length * 2;
            total += (len + 3) & ~3;
        }
        var data = new byte[total];
        int p = 0;
        foreach (var c in curves)
        {
            data[p] = (byte)'c'; data[p + 1] = (byte)'u'; data[p + 2] = (byte)'r'; data[p + 3] = (byte)'v';
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(p + 8, 4), (uint)c.Length);
            for (int i = 0; i < c.Length; i++)
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(p + 12 + i * 2, 2), c[i]);
            int len = 12 + c.Length * 2;
            p += (len + 3) & ~3;
        }
        return data;
    }

    private static byte[] BuildMatrixStream(double[,] m)
    {
        // 9 multiplier + 3 offset = 12 s15Fixed16 values.
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
        data[16] = 2;  // precision = 16-bit
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

    // ---- mAB reader sanity ----

    [Fact]
    public void IccProfile_GetTagLutAB_ParsesMatrixOnly()
    {
        var matrix = new double[3, 4]
        {
            { 0.5, 0.0, 0.0, 0.1 },
            { 0.0, 0.5, 0.0, 0.2 },
            { 0.0, 0.0, 0.5, 0.3 },
        };
        var mab = BuildLutAB(isAtoB: true, inCh: 3, outCh: 3, matrix: matrix);
        var prof = new VipsIccProfile();
        prof.SetTagData("A2B0", mab);
        var parsed = prof.GetTagLutAB("A2B0");
        Assert.NotNull(parsed);
        Assert.True(parsed!.IsAtoB);
        Assert.Equal(3, parsed.InputChannels);
        Assert.Equal(3, parsed.OutputChannels);
        Assert.NotNull(parsed.Matrix);
        Assert.Equal(0.5, parsed.Matrix![0, 0], 4);
        Assert.Equal(0.1, parsed.Matrix[0, 3], 4);
        Assert.Null(parsed.ACurves);
        Assert.Null(parsed.BCurves);
        Assert.Null(parsed.Clut);
    }

    [Fact]
    public void IccProfile_GetTagLutAB_ParsesMBA()
    {
        var matrix = new double[3, 4] { { 1, 0, 0, 0 }, { 0, 1, 0, 0 }, { 0, 0, 1, 0 } };
        var mba = BuildLutAB(isAtoB: false, inCh: 3, outCh: 3, matrix: matrix);
        var prof = new VipsIccProfile();
        prof.SetTagData("B2A0", mba);
        var parsed = prof.GetTagLutAB("B2A0");
        Assert.NotNull(parsed);
        Assert.False(parsed!.IsAtoB);
    }

    [Fact]
    public void IccProfile_GetTagLutAB_ParsesAllComponents()
    {
        // Identity-ish curves.
        var idCurve = new ushort[256];
        for (int i = 0; i < 256; i++) idCurve[i] = (ushort)(i * 65535 / 255);
        var aCurves = new[] { idCurve, idCurve, idCurve };
        var bCurves = new[] { idCurve, idCurve, idCurve };
        var mCurves = new[] { idCurve, idCurve, idCurve };

        var matrix = new double[3, 4] { { 1, 0, 0, 0 }, { 0, 1, 0, 0 }, { 0, 0, 1, 0 } };
        // Tiny CLUT (2×2×2) — barely interpolates.
        var clutData = new ushort[2 * 2 * 2 * 3];
        var grids = new[] { 2, 2, 2 };
        var mab = BuildLutAB(isAtoB: true, inCh: 3, outCh: 3,
            aCurves: aCurves, bCurves: bCurves, mCurves: mCurves,
            matrix: matrix, clut: (grids, clutData));
        var prof = new VipsIccProfile();
        prof.SetTagData("A2B0", mab);
        var parsed = prof.GetTagLutAB("A2B0");
        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.ACurves);
        Assert.NotNull(parsed.BCurves);
        Assert.NotNull(parsed.MCurves);
        Assert.NotNull(parsed.Matrix);
        Assert.NotNull(parsed.Clut);
        Assert.Equal(3, parsed.ACurves!.Length);
    }

    // ---- LUT pipeline round trip via VipsIccLutCmm ----

    [Fact]
    public void LutCmm_MatrixOnly_RoundTripsViaInverseMatrix()
    {
        // src.mAB applies a halving matrix; dst.mBA applies a doubling
        // matrix. Round-trip should land near identity.
        var halve = new double[3, 4] { { 0.5, 0, 0, 0 }, { 0, 0.5, 0, 0 }, { 0, 0, 0.5, 0 } };
        var doubl = new double[3, 4] { { 2.0, 0, 0, 0 }, { 0, 2.0, 0, 0 }, { 0, 0, 2.0, 0 } };
        var srcMab = BuildLutAB(isAtoB: true, inCh: 3, outCh: 3, matrix: halve);
        var dstMba = BuildLutAB(isAtoB: false, inCh: 3, outCh: 3, matrix: doubl);
        // Both A2B0 and B2A0 of source profile use mAB; we read A2B0.
        // For dest we need B2A0 to be mBA.
        var srcProfBytes = BuildSyntheticProfile(srcMab, srcMab);
        var dstProfBytes = BuildSyntheticProfile(dstMba, dstMba);

        var src = VipsIccProfile.TryParse(srcProfBytes)!;
        var dst = VipsIccProfile.TryParse(dstProfBytes)!;
        var cmm = VipsIccLutCmm.TryBuild(src, dst);
        Assert.NotNull(cmm);

        // Use values in the safe range — halve+double round trip stays
        // within byte-rounding tolerance.
        var input = new byte[16 * 3];
        for (int i = 0; i < 16; i++)
        {
            input[i * 3] = (byte)(50 + i * 10);
            input[i * 3 + 1] = (byte)(30 + i * 12);
            input[i * 3 + 2] = (byte)(10 + i * 14);
        }
        var output = new byte[input.Length];
        cmm!.Apply(input, 0, output, 0, 16, bands: 3);

        for (int i = 0; i < input.Length; i++)
        {
            int delta = Math.Abs(input[i] - output[i]);
            Assert.True(delta <= 2, $"byte {i}: in={input[i]} out={output[i]} delta={delta}");
        }
    }

    [Fact]
    public void LutCmm_TryBuildsForMixedTagTypes()
    {
        // Source A2B0 = mAB, dest B2A0 = mft2 (legacy lut16Type) — TryBuild
        // must accept the mix, since LutTransform.TryFromTag prefers
        // whichever tag form is present.
        var matrix = new double[3, 4] { { 1, 0, 0, 0 }, { 0, 1, 0, 0 }, { 0, 0, 1, 0 } };
        var idCurve = new ushort[256];
        for (int i = 0; i < 256; i++) idCurve[i] = (ushort)(i * 65535 / 255);
        var srcMab = BuildLutAB(isAtoB: true, inCh: 3, outCh: 3, matrix: matrix,
            aCurves: new[] { idCurve, idCurve, idCurve },
            bCurves: new[] { idCurve, idCurve, idCurve });

        // Dest with mft2 in B2A0 — borrow Round 121's halve-mft2 form.
        var dstMft2 = BuildIdentityMft2();

        var srcProf = BuildSyntheticProfile(srcMab, srcMab);
        var dstProf = BuildSyntheticProfile(dstMft2, dstMft2);
        var src = VipsIccProfile.TryParse(srcProf)!;
        var dst = VipsIccProfile.TryParse(dstProf)!;

        var cmm = VipsIccLutCmm.TryBuild(src, dst);
        Assert.NotNull(cmm);
    }

    /// <summary>Build an mft2 that's an identity transform.</summary>
    private static byte[] BuildIdentityMft2()
    {
        // Same shape as Round 121's BuildMft2, inlined for self-containment.
        int inCh = 3, outCh = 3, grid = 5, n = 256;
        int clutEntries = grid * grid * grid;
        int size = 52 + 2 * inCh * n + 2 * clutEntries * outCh + 2 * outCh * n;
        var data = new byte[size];
        data[0] = (byte)'m'; data[1] = (byte)'f'; data[2] = (byte)'t'; data[3] = (byte)'2';
        data[8] = (byte)inCh; data[9] = (byte)outCh; data[10] = (byte)grid;
        // Identity matrix.
        for (int i = 0; i < 3; i++)
            WriteS15Fixed16(data, 12 + (i * 3 + i) * 4, 1.0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(48, 2), (ushort)n);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(50, 2), (ushort)n);

        int p = 52;
        for (int c = 0; c < inCh; c++)
            for (int k = 0; k < n; k++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(p, 2), (ushort)(k * 65535 / 255));
                p += 2;
            }
        // Identity CLUT: out value at (a, b, c) is the input value scaled
        // back to 0..65535.
        for (int a = 0; a < grid; a++)
            for (int b = 0; b < grid; b++)
                for (int cc = 0; cc < grid; cc++)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(p, 2), (ushort)(a * 65535 / (grid - 1))); p += 2;
                    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(p, 2), (ushort)(b * 65535 / (grid - 1))); p += 2;
                    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(p, 2), (ushort)(cc * 65535 / (grid - 1))); p += 2;
                }
        for (int c = 0; c < outCh; c++)
            for (int k = 0; k < n; k++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(p, 2), (ushort)(k * 65535 / 255));
                p += 2;
            }
        return data;
    }

    [Fact]
    public void LutCmm_BCurvesOnly_AppliesCurvesInBothDirections()
    {
        // mAB with only B curves: input → matrix(none) → B curves.
        // mBA with only B curves: input → B curves → matrix(none).
        // Round trip with inverse curves should produce identity.
        var halveCurve = new ushort[256];
        var doubleCurve = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            halveCurve[i] = (ushort)(i * 65535 / 255 / 2);          // out = in/2
            doubleCurve[i] = (ushort)Math.Min(65535, i * 65535 / 255 * 2);  // out = in*2 (clamped)
        }
        var srcMab = BuildLutAB(isAtoB: true, inCh: 3, outCh: 3,
            bCurves: new[] { halveCurve, halveCurve, halveCurve });
        var dstMba = BuildLutAB(isAtoB: false, inCh: 3, outCh: 3,
            bCurves: new[] { doubleCurve, doubleCurve, doubleCurve });
        var srcProf = BuildSyntheticProfile(srcMab, srcMab);
        var dstProf = BuildSyntheticProfile(dstMba, dstMba);

        var cmm = VipsIccLutCmm.TryBuild(VipsIccProfile.TryParse(srcProf)!, VipsIccProfile.TryParse(dstProf)!);
        Assert.NotNull(cmm);

        var input = new byte[8 * 3];
        for (int i = 0; i < 8; i++)
        {
            input[i * 3]     = (byte)(40 + i * 8);
            input[i * 3 + 1] = (byte)(60 + i * 6);
            input[i * 3 + 2] = (byte)(20 + i * 12);
        }
        var output = new byte[input.Length];
        cmm!.Apply(input, 0, output, 0, 8, bands: 3);

        for (int i = 0; i < input.Length; i++)
        {
            int delta = Math.Abs(input[i] - output[i]);
            Assert.True(delta <= 3, $"byte {i}: in={input[i]} out={output[i]} delta={delta}");
        }
    }
}
