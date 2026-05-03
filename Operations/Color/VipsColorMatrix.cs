using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// 4×4 colour matrix transform on RGBA pixels. Mirrors ImageSharp's
/// <c>Filter(ColorMatrix)</c> processor. Differs from
/// <see cref="VipsRecomb"/> (3×3 RGB-only) in that the alpha channel
/// is part of the mix — useful for effects where alpha mixes with
/// colour (premultiplied dimming, alpha-preserving colour grades,
/// brightness shifts via the +translation column).
///
/// <para>Output formula per pixel:</para>
/// <code>
/// out.R = m[0,0]·R + m[0,1]·G + m[0,2]·B + m[0,3]·A + m[0,4]
/// out.G = m[1,0]·R + m[1,1]·G + m[1,2]·B + m[1,3]·A + m[1,4]
/// out.B = m[2,0]·R + m[2,1]·G + m[2,2]·B + m[2,3]·A + m[2,4]
/// out.A = m[3,0]·R + m[3,1]·G + m[3,2]·B + m[3,3]·A + m[3,4]
/// </code>
///
/// <para>Matrix is 4×5 (4 mix rows + 1 translation column). UChar
/// 4-band input clamps; Float 4-band passes through unclamped.</para>
/// </summary>
public class VipsColorMatrix : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    /// <summary>4×5 matrix: rows are output channels, columns are R, G, B, A, translation.</summary>
    public double[,]? Matrix { get; set; }

    public override int Build()
    {
        if (In == null || Matrix == null) return -1;
        if (Matrix.GetLength(0) != 4 || Matrix.GetLength(1) != 5) return -1;
        if (In.Bands != 4) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = 4, BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = Matrix,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("ColorMatrix"); h.Add(RuntimeHelpers.GetHashCode(In));
        if (Matrix != null) foreach (var v in Matrix) h.Add(v);
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inReg = (VipsRegion)seq!;
        var m = (double[,])b!;
        VipsRect r = outRegion.Valid;
        if (inReg.Prepare(r) != 0) return -1;

        bool isFloat = inReg.Image.BandFormat == VipsBandFormat.Float;
        for (int y = 0; y < r.Height; y++)
        {
            var ia = inReg.GetAddress(r.Left, r.Top + y);
            var oa = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                double R, G, B, A;
                if (isFloat)
                {
                    int o = x * 16;
                    R = BinaryPrimitives.ReadSingleLittleEndian(ia.Slice(o + 0, 4));
                    G = BinaryPrimitives.ReadSingleLittleEndian(ia.Slice(o + 4, 4));
                    B = BinaryPrimitives.ReadSingleLittleEndian(ia.Slice(o + 8, 4));
                    A = BinaryPrimitives.ReadSingleLittleEndian(ia.Slice(o + 12, 4));
                }
                else
                {
                    int o = x * 4;
                    R = ia[o + 0]; G = ia[o + 1]; B = ia[o + 2]; A = ia[o + 3];
                }

                double Ro = m[0, 0] * R + m[0, 1] * G + m[0, 2] * B + m[0, 3] * A + m[0, 4];
                double Go = m[1, 0] * R + m[1, 1] * G + m[1, 2] * B + m[1, 3] * A + m[1, 4];
                double Bo = m[2, 0] * R + m[2, 1] * G + m[2, 2] * B + m[2, 3] * A + m[2, 4];
                double Ao = m[3, 0] * R + m[3, 1] * G + m[3, 2] * B + m[3, 3] * A + m[3, 4];

                if (isFloat)
                {
                    int o = x * 16;
                    BinaryPrimitives.WriteSingleLittleEndian(oa.Slice(o + 0, 4), (float)Ro);
                    BinaryPrimitives.WriteSingleLittleEndian(oa.Slice(o + 4, 4), (float)Go);
                    BinaryPrimitives.WriteSingleLittleEndian(oa.Slice(o + 8, 4), (float)Bo);
                    BinaryPrimitives.WriteSingleLittleEndian(oa.Slice(o + 12, 4), (float)Ao);
                }
                else
                {
                    int o = x * 4;
                    oa[o + 0] = (byte)Math.Clamp(Math.Round(Ro), 0, 255);
                    oa[o + 1] = (byte)Math.Clamp(Math.Round(Go), 0, 255);
                    oa[o + 2] = (byte)Math.Clamp(Math.Round(Bo), 0, 255);
                    oa[o + 3] = (byte)Math.Clamp(Math.Round(Ao), 0, 255);
                }
            }
        }
        return 0;
    }
}
