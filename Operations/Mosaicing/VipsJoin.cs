using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Mosaicing;

/// <summary>
/// Paste two images side-by-side or top-to-bottom with optional
/// linear-blend at the seam. Mirrors libvips <c>vips_join</c>.
///
/// <para>For <see cref="VipsDirection.Horizontal"/> the output is
/// <c>(A.W + B.W − Shim, max(A.H, B.H))</c>; <see cref="Align"/>
/// controls the cross-axis (vertical) placement of the shorter input.
/// <c>Shim</c> &gt; 0 enables a linear-alpha blend over the last
/// <c>Shim</c> columns of A overlapping the first <c>Shim</c> columns
/// of B — the simplest seam-hiding strategy. Both inputs must agree
/// on band count and band format.</para>
/// </summary>
public class VipsJoin : VipsOperation
{
    public VipsImage? Left { get; set; }
    public VipsImage? Right { get; set; }
    public VipsImage? Out { get; set; }
    public VipsDirection Direction { get; set; } = VipsDirection.Horizontal;
    /// <summary>Linear-blend overlap width (px). 0 = hard seam.</summary>
    public int Shim { get; set; } = 0;
    public VipsAlign Align { get; set; } = VipsAlign.Low;
    public double[]? Background { get; set; }

    public override int Build()
    {
        if (Left == null || Right == null) return -1;
        if (Left.Bands != Right.Bands || Left.BandFormat != Right.BandFormat) return -1;
        if (Shim < 0) return -1;

        int outW, outH, ax, ay, bx, by;
        if (Direction == VipsDirection.Horizontal)
        {
            outW = Left.Width + Right.Width - Shim;
            outH = Math.Max(Left.Height, Right.Height);
            ax = 0; ay = AlignOffset(Align, outH - Left.Height);
            bx = Left.Width - Shim; by = AlignOffset(Align, outH - Right.Height);
        }
        else
        {
            outW = Math.Max(Left.Width, Right.Width);
            outH = Left.Height + Right.Height - Shim;
            ax = AlignOffset(Align, outW - Left.Width); ay = 0;
            bx = AlignOffset(Align, outW - Right.Width); by = Left.Height - Shim;
        }

        Out = new VipsImage
        {
            Width = outW, Height = outH, Bands = Left.Bands, BandFormat = Left.BandFormat,
            Interpretation = Left.Interpretation,
            Coding = Left.Coding, XRes = Left.XRes, YRes = Left.YRes,
            StartFn = VipsSeq.StartMany, GenerateFn = Generate, StopFn = VipsSeq.StopMany,
            ClientA = new[] { Left, Right },
            ClientB = (ax, ay, bx, by, Direction, Shim,
                Background ?? new double[Left.Bands]),
        };
        Out.CopyMetadataFrom(Left);
        Out.SetPipeline(VipsDemandStyle.Any, Left, Right);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Join", RuntimeHelpers.GetHashCode(Left),
            RuntimeHelpers.GetHashCode(Right), Direction, Shim, Align);

    private static int AlignOffset(VipsAlign align, int slack)
        => align switch { VipsAlign.Centre => slack / 2, VipsAlign.High => slack, _ => 0 };

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var (ax, ay, bx, by, dir, shim, background) =
            ((int, int, int, int, VipsDirection, int, double[]))b!;

        VipsImage outImg = outRegion.Image;
        VipsImage A = regions[0].Image, B = regions[1].Image;
        VipsRect r = outRegion.Valid;
        bool isFloat = outImg.BandFormat == VipsBandFormat.Float;
        int sampleSize = isFloat ? 4 : 1;
        int bands = outImg.Bands;
        int pelBytes = bands * sampleSize;

        // Background fill first.
        var bgPel = new byte[pelBytes];
        if (isFloat)
        {
            for (int bnd = 0; bnd < bands; bnd++)
                BinaryPrimitives.WriteSingleLittleEndian(
                    bgPel.AsSpan(bnd * 4, 4), (float)background[bnd]);
        }
        else
        {
            for (int bnd = 0; bnd < bands; bnd++)
                bgPel[bnd] = (byte)Math.Clamp(background[bnd], 0, 255);
        }
        for (int y = 0; y < r.Height; y++)
        {
            var addr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
                bgPel.AsSpan().CopyTo(addr.Slice(x * pelBytes, pelBytes));
        }

        // Prepare overlapping slabs for A and B.
        if (PrepareSlab(regions[0], A, ax, ay, r, out var aRect, out bool aHas) != 0) return -1;
        if (PrepareSlab(regions[1], B, bx, by, r, out var bRect, out bool bHas) != 0) return -1;

        // Write A first, then B (B wins on overlap unless we're blending).
        if (aHas) CopySlab(regions[0], outRegion, A, ax, ay, aRect, r, pelBytes);
        if (bHas)
        {
            if (shim == 0)
            {
                CopySlab(regions[1], outRegion, B, bx, by, bRect, r, pelBytes);
            }
            else
            {
                BlendSlab(regions[1], outRegion, B, ax, ay, bx, by, bRect, r,
                    dir, shim, regions[0], A, isFloat, sampleSize, bands, pelBytes);
            }
        }
        return 0;
    }

    private static int PrepareSlab(VipsRegion reg, VipsImage img, int ox, int oy,
        VipsRect r, out VipsRect inRect, out bool any)
    {
        int x0 = Math.Max(r.Left, ox);
        int y0 = Math.Max(r.Top, oy);
        int x1 = Math.Min(r.Left + r.Width, ox + img.Width);
        int y1 = Math.Min(r.Top + r.Height, oy + img.Height);
        if (x0 >= x1 || y0 >= y1) { inRect = default; any = false; return 0; }
        inRect = new VipsRect(x0 - ox, y0 - oy, x1 - x0, y1 - y0);
        any = true;
        return reg.Prepare(inRect);
    }

    private static void CopySlab(VipsRegion src, VipsRegion dst, VipsImage img,
        int ox, int oy, VipsRect inRect, VipsRect r, int pelBytes)
    {
        int rowBytes = inRect.Width * pelBytes;
        int dstX = inRect.Left + ox, dstY = inRect.Top + oy;
        for (int sy = 0; sy < inRect.Height; sy++)
        {
            var inAddr = src.GetAddress(inRect.Left, inRect.Top + sy);
            var outAddr = dst.GetAddress(dstX, dstY + sy);
            inAddr.Slice(0, rowBytes).CopyTo(outAddr);
        }
    }

    private static void BlendSlab(VipsRegion srcB, VipsRegion dst, VipsImage Bimg,
        int ax, int ay, int bx, int by, VipsRect bRect, VipsRect r,
        VipsDirection dir, int shim,
        VipsRegion srcA, VipsImage Aimg,
        bool isFloat, int sampleSize, int bands, int pelBytes)
    {
        // For each pel of B's slab, compute alpha based on its position
        // along the seam axis. Outside the seam window, just copy B.
        int dstX = bRect.Left + bx, dstY = bRect.Top + by;
        for (int sy = 0; sy < bRect.Height; sy++)
        {
            var bAddr = srcB.GetAddress(bRect.Left, bRect.Top + sy);
            var outAddr = dst.GetAddress(dstX, dstY + sy);
            for (int sx = 0; sx < bRect.Width; sx++)
            {
                int gx = dstX + sx, gy = dstY + sy;
                double alpha = 1.0; // weight of B
                if (dir == VipsDirection.Horizontal)
                {
                    int seamX0 = bx; // first column of B = first column of seam
                    int seamX1 = bx + shim;
                    if (gx >= seamX0 && gx < seamX1)
                        alpha = (gx - seamX0 + 1) / (double)(shim + 1);
                }
                else
                {
                    int seamY0 = by;
                    int seamY1 = by + shim;
                    if (gy >= seamY0 && gy < seamY1)
                        alpha = (gy - seamY0 + 1) / (double)(shim + 1);
                }

                if (alpha >= 1.0)
                {
                    bAddr.Slice(sx * pelBytes, pelBytes).CopyTo(outAddr.Slice(sx * pelBytes, pelBytes));
                    continue;
                }
                // Blend with whatever's already in `outAddr` (A was written first).
                if (isFloat)
                {
                    for (int bnd = 0; bnd < bands; bnd++)
                    {
                        int off = sx * pelBytes + bnd * 4;
                        float aV = BinaryPrimitives.ReadSingleLittleEndian(outAddr.Slice(off, 4));
                        float bV = BinaryPrimitives.ReadSingleLittleEndian(bAddr.Slice(off, 4));
                        float v = (float)((1 - alpha) * aV + alpha * bV);
                        BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(off, 4), v);
                    }
                }
                else
                {
                    for (int bnd = 0; bnd < bands; bnd++)
                    {
                        int off = sx * pelBytes + bnd;
                        int aV = outAddr[off];
                        int bV = bAddr[off];
                        int v = (int)Math.Round((1 - alpha) * aV + alpha * bV);
                        outAddr[off] = (byte)Math.Clamp(v, 0, 255);
                    }
                }
            }
        }
    }
}
