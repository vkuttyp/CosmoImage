using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

public enum VipsBooleanOperation
{
    And = 0,
    Or = 1,
    Xor = 2,
    LShift = 3,
    RShift = 4,
}

public enum VipsRelationalOperation
{
    Equal = 0,
    NotEqual = 1,
    Less = 2,
    LessEq = 3,
    More = 4,
    MoreEq = 5,
}

/// <summary>
/// Bitwise boolean op against a per-band constant. Output is UChar with the
/// bitwise result. For image-vs-image use <see cref="VipsBoolean2"/>.
/// Mirrors libvips <c>vips_boolean_const</c>.
/// </summary>
public class VipsBooleanConst : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public VipsBooleanOperation Op { get; set; }
    public double[]? C { get; set; }

    public override int Build()
    {
        if (In == null || C == null) return -1;
        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = In.Bands,
            BandFormat = In.BandFormat, Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = new { Op, C }
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("BooleanConst"); h.Add(RuntimeHelpers.GetHashCode(In)); h.Add(Op);
        if (C != null) foreach (var c in C) h.Add(c);
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        dynamic config = b!;
        VipsBooleanOperation op = config.Op;
        double[] C = config.C;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        int bands = @in.Bands;
        int totalBytes = r.Width * bands;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int i = 0; i < totalBytes; i++)
            {
                byte rhs = (byte)Math.Clamp(C[i % bands % C.Length], 0, 255);
                byte lhs = inAddr[i];
                outAddr[i] = op switch
                {
                    VipsBooleanOperation.And => (byte)(lhs & rhs),
                    VipsBooleanOperation.Or => (byte)(lhs | rhs),
                    VipsBooleanOperation.Xor => (byte)(lhs ^ rhs),
                    VipsBooleanOperation.LShift => (byte)Math.Clamp(lhs << (rhs & 7), 0, 255),
                    VipsBooleanOperation.RShift => (byte)(lhs >> (rhs & 7)),
                    _ => lhs
                };
            }
        }
        return 0;
    }
}

/// <summary>
/// Per-pixel relational op against a per-band constant. Output is UChar:
/// 255 where the relation holds, 0 otherwise — the libvips convention so
/// the result composes cleanly with bitwise mask ops.
/// </summary>
public class VipsRelationalConst : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public VipsRelationalOperation Op { get; set; }
    public double[]? C { get; set; }

    public override int Build()
    {
        if (In == null || C == null) return -1;
        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = In.Bands,
            BandFormat = In.BandFormat, Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = new { Op, C }
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("RelationalConst"); h.Add(RuntimeHelpers.GetHashCode(In)); h.Add(Op);
        if (C != null) foreach (var c in C) h.Add(c);
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        dynamic config = b!;
        VipsRelationalOperation op = config.Op;
        double[] C = config.C;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        int bands = @in.Bands;
        int totalBytes = r.Width * bands;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int i = 0; i < totalBytes; i++)
            {
                double rhs = C[i % bands % C.Length];
                double lhs = inAddr[i];
                bool hit = op switch
                {
                    VipsRelationalOperation.Equal => lhs == rhs,
                    VipsRelationalOperation.NotEqual => lhs != rhs,
                    VipsRelationalOperation.Less => lhs < rhs,
                    VipsRelationalOperation.LessEq => lhs <= rhs,
                    VipsRelationalOperation.More => lhs > rhs,
                    VipsRelationalOperation.MoreEq => lhs >= rhs,
                    _ => false
                };
                outAddr[i] = (byte)(hit ? 255 : 0);
            }
        }
        return 0;
    }
}

/// <summary>
/// Two-image bitwise boolean op. Inputs must have matching dimensions and
/// band count. Mirrors libvips <c>vips_boolean</c>.
/// </summary>
public class VipsBoolean2 : VipsOperation
{
    public VipsImage? Left { get; set; }
    public VipsImage? Right { get; set; }
    public VipsImage? Out { get; set; }
    public VipsBooleanOperation Op { get; set; }

    public override int Build()
    {
        if (Left == null || Right == null) return -1;
        if (Left.Width != Right.Width || Left.Height != Right.Height || Left.Bands != Right.Bands)
            return -1;

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
        => HashCode.Combine("Boolean2", RuntimeHelpers.GetHashCode(Left), RuntimeHelpers.GetHashCode(Right), Op);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var lhs = regions[0]; var rhs = regions[1];
        VipsBooleanOperation op = (VipsBooleanOperation)b!;
        VipsRect r = outRegion.Valid;

        if (lhs.Prepare(r) != 0) return -1;
        if (rhs.Prepare(r) != 0) return -1;

        int totalBytes = r.Width * lhs.Image.Bands;

        for (int y = 0; y < r.Height; y++)
        {
            var la = lhs.GetAddress(r.Left, r.Top + y);
            var ra = rhs.GetAddress(r.Left, r.Top + y);
            var oa = outRegion.GetAddress(r.Left, r.Top + y);
            for (int i = 0; i < totalBytes; i++)
            {
                oa[i] = op switch
                {
                    VipsBooleanOperation.And => (byte)(la[i] & ra[i]),
                    VipsBooleanOperation.Or => (byte)(la[i] | ra[i]),
                    VipsBooleanOperation.Xor => (byte)(la[i] ^ ra[i]),
                    VipsBooleanOperation.LShift => (byte)Math.Clamp(la[i] << (ra[i] & 7), 0, 255),
                    VipsBooleanOperation.RShift => (byte)(la[i] >> (ra[i] & 7)),
                    _ => la[i]
                };
            }
        }
        return 0;
    }
}

/// <summary>
/// Two-image relational op. Output is UChar (255/0 per pixel and band).
/// Mirrors libvips <c>vips_relational</c>.
/// </summary>
public class VipsRelational2 : VipsOperation
{
    public VipsImage? Left { get; set; }
    public VipsImage? Right { get; set; }
    public VipsImage? Out { get; set; }
    public VipsRelationalOperation Op { get; set; }

    public override int Build()
    {
        if (Left == null || Right == null) return -1;
        if (Left.Width != Right.Width || Left.Height != Right.Height || Left.Bands != Right.Bands)
            return -1;

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
        => HashCode.Combine("Relational2", RuntimeHelpers.GetHashCode(Left), RuntimeHelpers.GetHashCode(Right), Op);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var lhs = regions[0]; var rhs = regions[1];
        VipsRelationalOperation op = (VipsRelationalOperation)b!;
        VipsRect r = outRegion.Valid;

        if (lhs.Prepare(r) != 0) return -1;
        if (rhs.Prepare(r) != 0) return -1;

        int totalBytes = r.Width * lhs.Image.Bands;

        for (int y = 0; y < r.Height; y++)
        {
            var la = lhs.GetAddress(r.Left, r.Top + y);
            var ra = rhs.GetAddress(r.Left, r.Top + y);
            var oa = outRegion.GetAddress(r.Left, r.Top + y);
            for (int i = 0; i < totalBytes; i++)
            {
                bool hit = op switch
                {
                    VipsRelationalOperation.Equal => la[i] == ra[i],
                    VipsRelationalOperation.NotEqual => la[i] != ra[i],
                    VipsRelationalOperation.Less => la[i] < ra[i],
                    VipsRelationalOperation.LessEq => la[i] <= ra[i],
                    VipsRelationalOperation.More => la[i] > ra[i],
                    VipsRelationalOperation.MoreEq => la[i] >= ra[i],
                    _ => false
                };
                oa[i] = (byte)(hit ? 255 : 0);
            }
        }
        return 0;
    }
}
