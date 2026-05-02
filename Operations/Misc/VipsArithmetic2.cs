using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

public enum VipsArith2Op
{
    Add = 0,
    Subtract = 1,
    Multiply = 2,
    Divide = 3,
    Remainder = 4,
}

/// <summary>
/// Image-image binary arithmetic: <c>out = left op right</c> per pixel.
/// Inputs must agree on width / height / band count. UChar branch clamps
/// to [0, 255] (and divides by 255 for the multiply path so the operation
/// behaves as fraction-fraction multiplication, matching libvips' UChar
/// convention). Float branch is unclamped — the libvips Float behaviour
/// where intermediate signal values can exceed nominal [0, 1].
///
/// <para>Mirrors libvips <c>vips_add</c> / <c>vips_subtract</c> /
/// <c>vips_multiply</c> / <c>vips_divide</c> / <c>vips_remainder</c>
/// (the binary.c family). Constant-vs-image variants are covered by
/// <see cref="CosmoImage.Operations.Color.VipsLinear"/>.</para>
/// </summary>
public class VipsArithmetic2 : VipsOperation
{
    public VipsImage? Left { get; set; }
    public VipsImage? Right { get; set; }
    public VipsImage? Out { get; set; }
    public VipsArith2Op Op { get; set; }

    public override int Build()
    {
        if (Left == null || Right == null) return -1;
        if (Left.Width != Right.Width || Left.Height != Right.Height || Left.Bands != Right.Bands)
            return -1;
        if (Left.BandFormat != Right.BandFormat) return -1;

        Out = new VipsImage
        {
            Width = Left.Width, Height = Left.Height, Bands = Left.Bands,
            BandFormat = Left.BandFormat, Interpretation = Left.Interpretation,
            Coding = Left.Coding, XRes = Left.XRes, YRes = Left.YRes,
            StartFn = VipsSeq.StartMany, GenerateFn = Generate, StopFn = VipsSeq.StopMany,
            ClientA = new[] { Left, Right }, ClientB = Op
        };
        Out.CopyMetadataFrom(Left);
        Out.SetPipeline(VipsDemandStyle.Any, Left, Right);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Arithmetic2", RuntimeHelpers.GetHashCode(Left), RuntimeHelpers.GetHashCode(Right), Op);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var lhs = regions[0];
        var rhs = regions[1];
        VipsImage left = lhs.Image;
        VipsArith2Op op = (VipsArith2Op)b!;
        VipsRect r = outRegion.Valid;

        if (lhs.Prepare(r) != 0) return -1;
        if (rhs.Prepare(r) != 0) return -1;

        if (left.BandFormat == VipsBandFormat.Float)
            return GenerateFloat(lhs, rhs, outRegion, r, left.Bands, op);

        return GenerateUChar(lhs, rhs, outRegion, r, left.Bands, op);
    }

    private static int GenerateUChar(VipsRegion lhs, VipsRegion rhs, VipsRegion outRegion, VipsRect r, int bands, VipsArith2Op op)
    {
        int totalBytes = r.Width * bands;
        for (int y = 0; y < r.Height; y++)
        {
            var la = lhs.GetAddress(r.Left, r.Top + y);
            var ra = rhs.GetAddress(r.Left, r.Top + y);
            var oa = outRegion.GetAddress(r.Left, r.Top + y);
            for (int i = 0; i < totalBytes; i++)
            {
                int l = la[i];
                int rv = ra[i];
                int res = op switch
                {
                    VipsArith2Op.Add => l + rv,
                    VipsArith2Op.Subtract => l - rv,
                    // UChar multiply: treat both as fractions of 255 so the
                    // pipeline behaves like masking (e.g. rgb * mask). libvips
                    // does the same — `vips_multiply` on UChar is fraction-mul.
                    VipsArith2Op.Multiply => (l * rv + 127) / 255,
                    VipsArith2Op.Divide => rv == 0 ? 0 : (l * 255) / rv,
                    VipsArith2Op.Remainder => rv == 0 ? 0 : l % rv,
                    _ => l
                };
                oa[i] = (byte)Math.Clamp(res, 0, 255);
            }
        }
        return 0;
    }

    private static int GenerateFloat(VipsRegion lhs, VipsRegion rhs, VipsRegion outRegion, VipsRect r, int bands, VipsArith2Op op)
    {
        for (int y = 0; y < r.Height; y++)
        {
            var la = lhs.GetAddress(r.Left, r.Top + y);
            var ra = rhs.GetAddress(r.Left, r.Top + y);
            var oa = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    int off = (x * bands + bnd) * 4;
                    float l = BinaryPrimitives.ReadSingleLittleEndian(la.Slice(off, 4));
                    float rv = BinaryPrimitives.ReadSingleLittleEndian(ra.Slice(off, 4));
                    float res = op switch
                    {
                        VipsArith2Op.Add => l + rv,
                        VipsArith2Op.Subtract => l - rv,
                        // Float multiply: direct value mul (no /255 scaling).
                        // Float values are already in nominal mathematical units.
                        VipsArith2Op.Multiply => l * rv,
                        VipsArith2Op.Divide => rv == 0 ? 0 : l / rv,
                        VipsArith2Op.Remainder => rv == 0 ? 0 : l % rv,
                        _ => l
                    };
                    BinaryPrimitives.WriteSingleLittleEndian(oa.Slice(off, 4), res);
                }
            }
        }
        return 0;
    }
}
