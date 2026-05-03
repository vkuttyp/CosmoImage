using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Geometric;

/// <summary>
/// Generic image remap. <see cref="Index"/> is a Float 2-band image
/// where each pixel <c>(x, y)</c> holds the (sx, sy) source-coordinate
/// to sample from <see cref="In"/>. Output dimensions match the
/// index image; band count and format inherit from the input.
/// Mirrors libvips <c>vips_mapim</c>.
///
/// <para>This is the workhorse for any spatial warp — lens-correction,
/// barrel-distort, fisheye-undistort, mesh-deformation. Combine
/// <see cref="Operations.Create.VipsXyz"/> with arithmetic
/// (Add, Linear, Cast) to express any coordinate transform, then run
/// it through Mapim.</para>
///
/// <para>Sampling is bilinear; out-of-bounds source coordinates emit
/// a configurable <see cref="Background"/> fill (default zero).</para>
/// </summary>
public class VipsMapim : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Index { get; set; }
    public VipsImage? Out { get; set; }
    public double[]? Background { get; set; }

    public override int Build()
    {
        if (In == null || Index == null) return -1;
        if (Index.BandFormat != VipsBandFormat.Float || Index.Bands != 2) return -1;

        Out = new VipsImage
        {
            Width = Index.Width, Height = Index.Height,
            Bands = In.Bands, BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartMany, GenerateFn = Generate, StopFn = VipsSeq.StopMany,
            ClientA = new[] { In, Index },
            ClientB = Background ?? new double[In.Bands],
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.SmallTile, In, Index);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("Mapim"); h.Add(RuntimeHelpers.GetHashCode(In));
        h.Add(RuntimeHelpers.GetHashCode(Index));
        if (Background != null) foreach (var v in Background) h.Add(v);
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var inReg = regions[0];
        var idxReg = regions[1];
        var background = (double[])b!;
        VipsImage @in = inReg.Image;
        VipsRect r = outRegion.Valid;

        if (idxReg.Prepare(r) != 0) return -1;

        // Pre-pass: scan the index region to find the source-coordinate
        // bbox so we can prepare the input region in one shot rather than
        // per-pixel.
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        for (int y = 0; y < r.Height; y++)
        {
            var idxAddr = idxReg.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                double sx = BinaryPrimitives.ReadSingleLittleEndian(idxAddr.Slice(x * 8 + 0, 4));
                double sy = BinaryPrimitives.ReadSingleLittleEndian(idxAddr.Slice(x * 8 + 4, 4));
                if (sx < minX) minX = sx; if (sx > maxX) maxX = sx;
                if (sy < minY) minY = sy; if (sy > maxY) maxY = sy;
            }
        }
        int left = Math.Clamp((int)Math.Floor(minX), 0, @in.Width - 1);
        int top = Math.Clamp((int)Math.Floor(minY), 0, @in.Height - 1);
        int right = Math.Clamp((int)Math.Floor(maxX) + 2, 0, @in.Width);
        int bottom = Math.Clamp((int)Math.Floor(maxY) + 2, 0, @in.Height);
        if (right <= left || bottom <= top)
        {
            // Index covers nothing inside input — fill background and return.
            FillBackground(outRegion, r, background);
            return 0;
        }
        if (inReg.Prepare(new VipsRect(left, top, right - left, bottom - top)) != 0) return -1;

        bool isFloat = @in.BandFormat == VipsBandFormat.Float;
        int sampleSize = isFloat ? 4 : 1;
        int bands = @in.Bands;
        int pelBytes = bands * sampleSize;
        int W = @in.Width, H = @in.Height;

        for (int y = 0; y < r.Height; y++)
        {
            var idxAddr = idxReg.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                double sx = BinaryPrimitives.ReadSingleLittleEndian(idxAddr.Slice(x * 8 + 0, 4));
                double sy = BinaryPrimitives.ReadSingleLittleEndian(idxAddr.Slice(x * 8 + 4, 4));
                if (sx < 0 || sx > W - 1 || sy < 0 || sy > H - 1)
                {
                    WriteBackground(outAddr.Slice(x * pelBytes, pelBytes),
                        background, isFloat, bands);
                    continue;
                }
                int x0 = (int)Math.Floor(sx);
                int y0 = (int)Math.Floor(sy);
                int x1 = Math.Min(W - 1, x0 + 1);
                int y1 = Math.Min(H - 1, y0 + 1);
                double fx = sx - x0;
                double fy = sy - y0;
                BilinearSample(inReg, x0, y0, x1, y1, fx, fy,
                    outAddr.Slice(x * pelBytes, pelBytes), isFloat, bands);
            }
        }
        return 0;
    }

    private static void BilinearSample(VipsRegion inReg,
        int x0, int y0, int x1, int y1, double fx, double fy,
        Span<byte> outPel, bool isFloat, int bands)
    {
        var p00 = inReg.GetAddress(x0, y0);
        var p10 = inReg.GetAddress(x1, y0);
        var p01 = inReg.GetAddress(x0, y1);
        var p11 = inReg.GetAddress(x1, y1);
        if (isFloat)
        {
            for (int bnd = 0; bnd < bands; bnd++)
            {
                int off = bnd * 4;
                double v00 = BinaryPrimitives.ReadSingleLittleEndian(p00.Slice(off, 4));
                double v10 = BinaryPrimitives.ReadSingleLittleEndian(p10.Slice(off, 4));
                double v01 = BinaryPrimitives.ReadSingleLittleEndian(p01.Slice(off, 4));
                double v11 = BinaryPrimitives.ReadSingleLittleEndian(p11.Slice(off, 4));
                double v = (1 - fx) * (1 - fy) * v00 + fx * (1 - fy) * v10
                         + (1 - fx) * fy * v01 + fx * fy * v11;
                BinaryPrimitives.WriteSingleLittleEndian(outPel.Slice(off, 4), (float)v);
            }
        }
        else
        {
            for (int bnd = 0; bnd < bands; bnd++)
            {
                double v = (1 - fx) * (1 - fy) * p00[bnd]
                         + fx * (1 - fy) * p10[bnd]
                         + (1 - fx) * fy * p01[bnd]
                         + fx * fy * p11[bnd];
                outPel[bnd] = (byte)Math.Clamp(Math.Round(v), 0, 255);
            }
        }
    }

    private static void FillBackground(VipsRegion outRegion, VipsRect r, double[] background)
    {
        VipsImage o = outRegion.Image;
        bool isFloat = o.BandFormat == VipsBandFormat.Float;
        int bands = o.Bands;
        int pelBytes = bands * (isFloat ? 4 : 1);
        for (int y = 0; y < r.Height; y++)
        {
            var addr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
                WriteBackground(addr.Slice(x * pelBytes, pelBytes), background, isFloat, bands);
        }
    }

    private static void WriteBackground(Span<byte> pel, double[] background, bool isFloat, int bands)
    {
        if (isFloat)
        {
            for (int bnd = 0; bnd < bands; bnd++)
                BinaryPrimitives.WriteSingleLittleEndian(pel.Slice(bnd * 4, 4),
                    (float)(bnd < background.Length ? background[bnd] : 0));
        }
        else
        {
            for (int bnd = 0; bnd < bands; bnd++)
                pel[bnd] = (byte)Math.Clamp(bnd < background.Length ? background[bnd] : 0, 0, 255);
        }
    }
}
