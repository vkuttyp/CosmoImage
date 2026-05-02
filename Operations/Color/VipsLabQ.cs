using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Pack Float Lab into the libvips 4-byte LabQ encoding. Mirrors
/// <c>vips_Lab2LabQ</c>.
///
/// <para>The bit layout is:</para>
/// <list type="bullet">
///   <item>L: 10 bits unsigned (range 0..1023 → 0..100)</item>
///   <item>a: 11 bits signed (-1024..1023 → -128..128)</item>
///   <item>b: 11 bits signed (-1024..1023 → -128..128)</item>
/// </list>
/// <para>Stored as a 4-band UChar image:</para>
/// <list type="bullet">
///   <item>byte 0: high 8 bits of L (L &gt;&gt; 2)</item>
///   <item>byte 1: high 8 bits of a, signed (a &gt;&gt; 3)</item>
///   <item>byte 2: high 8 bits of b, signed (b &gt;&gt; 3)</item>
///   <item>byte 3: extension — <c>(L &amp; 3) &lt;&lt; 6 | (a &amp; 7) &lt;&lt; 3 | (b &amp; 7)</c></item>
/// </list>
///
/// <para>The encoding is what TIFF "ICCLAB" and many libvips on-disk
/// formats use; <see cref="VipsLabQ2Lab"/> is its exact inverse.</para>
/// </summary>
public class VipsLab2LabQ : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.Float || In.Bands != 3) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = 4, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.LabQ,
            Coding = VipsCoding.LabQ, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Lab2LabQ", RuntimeHelpers.GetHashCode(In));

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsRect r = outRegion.Valid;
        if (inRegion.Prepare(r) != 0) return -1;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                double L = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(x * 12 + 0, 4));
                double aa = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(x * 12 + 4, 4));
                double bb = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(x * 12 + 8, 4));

                // L scaled to 0..1023, a/b scaled to -1024..1023.
                int L10 = (int)Math.Clamp(Math.Round(L * 1023.0 / 100.0), 0, 1023);
                int A11 = (int)Math.Clamp(Math.Round(aa * 8.0), -1024, 1023);
                int B11 = (int)Math.Clamp(Math.Round(bb * 8.0), -1024, 1023);

                int outOff = x * 4;
                outAddr[outOff + 0] = (byte)(L10 >> 2);
                outAddr[outOff + 1] = (byte)((sbyte)(A11 >> 3)); // signed-byte high 8 bits
                outAddr[outOff + 2] = (byte)((sbyte)(B11 >> 3));
                outAddr[outOff + 3] = (byte)(((L10 & 3) << 6) | ((A11 & 7) << 3) | (B11 & 7));
            }
        }
        return 0;
    }
}

/// <summary>
/// Unpack libvips' 4-byte LabQ encoding back to Float Lab. Mirrors
/// <c>vips_LabQ2Lab</c>. See <see cref="VipsLab2LabQ"/> for the
/// bit layout.
/// </summary>
public class VipsLabQ2Lab : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar || In.Bands != 4) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = 3, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.Lab,
            Coding = VipsCoding.None, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("LabQ2Lab", RuntimeHelpers.GetHashCode(In));

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsRect r = outRegion.Valid;
        if (inRegion.Prepare(r) != 0) return -1;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int o = x * 4;
                byte b0 = inAddr[o + 0]; // L hi 8
                sbyte b1 = (sbyte)inAddr[o + 1]; // a hi 8 signed
                sbyte b2 = (sbyte)inAddr[o + 2]; // b hi 8 signed
                byte b3 = inAddr[o + 3]; // ext

                int L10 = (b0 << 2) | ((b3 >> 6) & 3);
                int A11 = (b1 << 3) | ((b3 >> 3) & 7);
                int B11 = (b2 << 3) | (b3 & 7);

                float L = (float)(L10 * 100.0 / 1023.0);
                float aa = A11 / 8.0f;
                float bb = B11 / 8.0f;

                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(x * 12 + 0, 4), L);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(x * 12 + 4, 4), aa);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(x * 12 + 8, 4), bb);
            }
        }
        return 0;
    }
}
