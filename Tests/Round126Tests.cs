using System;
using System.Buffers.Binary;
using CosmoImage.Operations.Color;
using CosmoImage.Operations.Metadata;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 126 — ICC rendering intents. Source A2B and dest B2A LUTs
/// can vary by intent (perceptual / relative-colorimetric /
/// saturation); the CMM now picks the right slot per the requested
/// intent and falls back to perceptual (A2B0/B2A0) when the
/// requested intent isn't present.
/// </summary>
public class Round126Tests
{
    /// <summary>
    /// Build a synthetic mft2 with a marker pattern so we can tell
    /// which intent's LUT was actually used at decode time.
    /// </summary>
    private static byte[] BuildMarkerMft2(int markerByte)
    {
        const int inCh = 3, outCh = 3, grid = 5;
        const int n = 256;
        int clutEntries = grid * grid * grid;
        int size = 52 + 2 * inCh * n + 2 * clutEntries * outCh + 2 * outCh * n;
        var data = new byte[size];

        data[0] = (byte)'m'; data[1] = (byte)'f'; data[2] = (byte)'t'; data[3] = (byte)'2';
        data[8] = (byte)inCh; data[9] = (byte)outCh; data[10] = (byte)grid;
        for (int i = 0; i < 3; i++) WriteS15Fixed16(data, 12 + (i * 3 + i) * 4, 1.0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(48, 2), n);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(50, 2), n);

        int p = 52;
        // Identity input curves.
        for (int c = 0; c < inCh; c++)
            for (int k = 0; k < n; k++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(p, 2), (ushort)(k * 65535 / 255));
                p += 2;
            }
        // CLUT: every entry produces (markerByte * 257) — the same constant
        // regardless of input. This makes "which LUT decoded?" trivial to
        // detect from the output bytes.
        for (int a = 0; a < grid; a++)
            for (int b = 0; b < grid; b++)
                for (int cc = 0; cc < grid; cc++)
                    for (int ch = 0; ch < outCh; ch++)
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(p, 2), (ushort)(markerByte * 257));
                        p += 2;
                    }
        // Identity output curves.
        for (int c = 0; c < outCh; c++)
            for (int k = 0; k < n; k++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(p, 2), (ushort)(k * 65535 / 255));
                p += 2;
            }
        return data;
    }

    private static void WriteS15Fixed16(byte[] buf, int off, double v)
    {
        int raw = (int)Math.Round(v * 65536.0);
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(off, 4), raw);
    }

    /// <summary>
    /// Build a profile with intent-tagged LUTs. Each A2B/B2A pair
    /// emits a distinct constant, so output bytes reveal which
    /// intent was selected.
    /// </summary>
    private static byte[] BuildMultiIntentProfile()
    {
        var prof = new VipsIccProfile { ConnectionColorSpace = VipsIccColorSpace.Xyz };
        prof.SetTagData("A2B0", BuildMarkerMft2(50));    // perceptual marker = 50
        prof.SetTagData("B2A0", BuildMarkerMft2(50));
        prof.SetTagData("A2B1", BuildMarkerMft2(150));   // colorimetric marker = 150
        prof.SetTagData("B2A1", BuildMarkerMft2(150));
        prof.SetTagData("A2B2", BuildMarkerMft2(220));   // saturation marker = 220
        prof.SetTagData("B2A2", BuildMarkerMft2(220));
        return prof.ToBytes();
    }

    private static byte[] BuildSingleIntentProfile()
    {
        // Only A2B0/B2A0 — older profiles with no intent-specific LUTs.
        var prof = new VipsIccProfile { ConnectionColorSpace = VipsIccColorSpace.Xyz };
        prof.SetTagData("A2B0", BuildMarkerMft2(70));
        prof.SetTagData("B2A0", BuildMarkerMft2(70));
        return prof.ToBytes();
    }

    private static byte ApplyAndProbe(byte[] srcProf, byte[] dstProf, VipsIccRenderingIntent intent)
    {
        var src = VipsIccProfile.TryParse(srcProf)!;
        var dst = VipsIccProfile.TryParse(dstProf)!;
        var cmm = VipsIccLutCmm.TryBuild(src, dst, intent);
        Assert.NotNull(cmm);
        // One pixel; CLUT is constant so the output is the marker byte
        // (modulo the dst LUT's transform, which is also constant in our
        // test fixture).
        var input = new byte[] { 100, 150, 200 };
        var output = new byte[3];
        cmm!.Apply(input, 0, output, 0, 1, bands: 3);
        return output[0];
    }

    [Fact]
    public void LutCmm_PerceptualIntent_UsesA2B0()
    {
        var prof = BuildMultiIntentProfile();
        // src outputs 50, dst pipeline applies LUT giving constant ≈ 50 too.
        // (the dst produces its marker constant regardless of input)
        byte b = ApplyAndProbe(prof, prof, VipsIccRenderingIntent.Perceptual);
        Assert.InRange(b, 48, 52);
    }

    [Fact]
    public void LutCmm_RelativeColorimetricIntent_UsesA2B1()
    {
        var prof = BuildMultiIntentProfile();
        byte b = ApplyAndProbe(prof, prof, VipsIccRenderingIntent.RelativeColorimetric);
        Assert.InRange(b, 148, 152);
    }

    [Fact]
    public void LutCmm_SaturationIntent_UsesA2B2()
    {
        var prof = BuildMultiIntentProfile();
        byte b = ApplyAndProbe(prof, prof, VipsIccRenderingIntent.Saturation);
        Assert.InRange(b, 218, 222);
    }

    [Fact]
    public void LutCmm_FallbackToPerceptual_WhenIntentTagMissing()
    {
        // Profile only has A2B0/B2A0 — requesting saturation should fall
        // back to perceptual rather than fail.
        var prof = BuildSingleIntentProfile();
        byte b = ApplyAndProbe(prof, prof, VipsIccRenderingIntent.Saturation);
        Assert.InRange(b, 68, 72);
    }
}
