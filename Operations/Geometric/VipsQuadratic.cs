using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Geometric;

/// <summary>
/// 2D quadratic-polynomial coordinate warp. For each output pixel
/// <c>(x, y)</c> the source coordinate is:
/// <code>
/// sx = a₀ + a₁·x + a₂·y + a₃·x² + a₄·x·y + a₅·y²
/// sy = b₀ + b₁·x + b₂·y + b₃·x² + b₄·x·y + b₅·y²
/// </code>
/// Mirrors libvips <c>vips_quadratic</c>.
///
/// <para>The standard model for second-order distortion correction:
/// chromatic aberration, lens curvature, mild barrel/pincushion warp.
/// For non-polynomial maps use <see cref="VipsMapim"/>; for pure
/// affine use <see cref="VipsAffine"/>.</para>
///
/// <para>Coefficients are passed as a length-12 <see cref="double[]"/>
/// in the order <c>[a₀, a₁, a₂, a₃, a₄, a₅, b₀, b₁, b₂, b₃, b₄, b₅]</c>.
/// Bilinear sampling; out-of-bounds pixels emit zero.</para>
/// </summary>
public class VipsQuadratic : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    /// <summary>12 coefficients: <c>[a0..a5, b0..b5]</c>.</summary>
    public double[]? Coefficients { get; set; }
    public int OutWidth { get; set; }
    public int OutHeight { get; set; }

    public override int Build()
    {
        if (In == null || Coefficients == null) return -1;
        if (Coefficients.Length != 12) return -1;

        int outW = OutWidth > 0 ? OutWidth : In.Width;
        int outH = OutHeight > 0 ? OutHeight : In.Height;

        Out = new VipsImage
        {
            Width = outW, Height = outH,
            Bands = In.Bands, BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = Coefficients,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.SmallTile, In);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("Quadratic"); h.Add(RuntimeHelpers.GetHashCode(In));
        h.Add(OutWidth); h.Add(OutHeight);
        if (Coefficients != null) foreach (var v in Coefficients) h.Add(v);
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inReg = (VipsRegion)seq!;
        VipsImage @in = inReg.Image;
        var c = (double[])b!;
        VipsRect r = outRegion.Valid;

        // Pre-pass: compute source bbox over the four output corners
        // (quadratic is monotone enough that corners give a usable
        // outer bound for our prefetch).
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        int[] cx = { r.Left, r.Right, r.Left, r.Right };
        int[] cy = { r.Top, r.Top, r.Bottom, r.Bottom };
        for (int i = 0; i < 4; i++)
        {
            var (sx, sy) = MapPoint(cx[i], cy[i], c);
            if (sx < minX) minX = sx; if (sx > maxX) maxX = sx;
            if (sy < minY) minY = sy; if (sy > maxY) maxY = sy;
        }
        int left = Math.Clamp((int)Math.Floor(minX), 0, @in.Width - 1);
        int top = Math.Clamp((int)Math.Floor(minY), 0, @in.Height - 1);
        int right = Math.Clamp((int)Math.Floor(maxX) + 2, 0, @in.Width);
        int bottom = Math.Clamp((int)Math.Floor(maxY) + 2, 0, @in.Height);
        if (right > left && bottom > top)
            if (inReg.Prepare(new VipsRect(left, top, right - left, bottom - top)) != 0) return -1;

        bool isFloat = @in.BandFormat == VipsBandFormat.Float;
        int bands = @in.Bands;
        int pelBytes = bands * (isFloat ? 4 : 1);
        int W = @in.Width, H = @in.Height;

        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                var (sx, sy) = MapPoint(r.Left + x, r.Top + y, c);
                if (sx < 0 || sx > W - 1 || sy < 0 || sy > H - 1)
                {
                    outAddr.Slice(x * pelBytes, pelBytes).Clear();
                    continue;
                }
                int x0 = (int)Math.Floor(sx);
                int y0 = (int)Math.Floor(sy);
                int x1 = Math.Min(W - 1, x0 + 1);
                int y1 = Math.Min(H - 1, y0 + 1);
                double fx = sx - x0, fy = sy - y0;
                Bilinear(inReg, x0, y0, x1, y1, fx, fy,
                    outAddr.Slice(x * pelBytes, pelBytes), isFloat, bands);
            }
        }
        return 0;
    }

    private static (double, double) MapPoint(double x, double y, double[] c)
    {
        double sx = c[0] + c[1] * x + c[2] * y + c[3] * x * x + c[4] * x * y + c[5] * y * y;
        double sy = c[6] + c[7] * x + c[8] * y + c[9] * x * x + c[10] * x * y + c[11] * y * y;
        return (sx, sy);
    }

    private static void Bilinear(VipsRegion inReg,
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
}
