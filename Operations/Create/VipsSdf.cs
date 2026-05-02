using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>The basic shape kinds supported by <see cref="VipsSdf"/>.</summary>
public enum VipsSdfShape
{
    Circle = 0,
    Box = 1,
    /// <summary>Axis-aligned rounded box; <see cref="VipsSdf.CornerRadius"/> sets the corner radius.</summary>
    RoundedBox = 2,
    Last = 3,
}

/// <summary>
/// Synthesise a signed-distance field for a basic shape. Each output
/// pixel is the (signed) Euclidean distance to the shape boundary —
/// negative inside, zero on the edge, positive outside. Mirrors
/// libvips <c>vips_sdf</c>. Float single-band output.
///
/// <para>SDFs are the modern primitive for crisp vector-shape
/// rasterisation (font glyphs, UI buttons, masks): threshold at 0
/// for a binary mask, smooth-step around 0 for an antialiased edge,
/// or use the value directly for outline / glow effects.</para>
/// </summary>
public class VipsSdf : VipsOperation
{
    public VipsImage? Out { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public VipsSdfShape Shape { get; set; } = VipsSdfShape.Circle;
    /// <summary>Centre x. Defaults to the image centre.</summary>
    public double Cx { get; set; } = double.NaN;
    public double Cy { get; set; } = double.NaN;
    /// <summary>Circle radius (Circle / RoundedBox use this); ignored for Box.</summary>
    public double Radius { get; set; } = 0;
    /// <summary>Half-extents for Box / RoundedBox.</summary>
    public double HalfWidth { get; set; } = 0;
    public double HalfHeight { get; set; } = 0;
    /// <summary>Rounded-box corner radius.</summary>
    public double CornerRadius { get; set; } = 0;

    public override int Build()
    {
        if (Width < 1 || Height < 1) return -1;
        double cx = double.IsNaN(Cx) ? Width / 2.0 : Cx;
        double cy = double.IsNaN(Cy) ? Height / 2.0 : Cy;

        Out = new VipsImage
        {
            Width = Width, Height = Height, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.BW,
            XRes = 1, YRes = 1,
            GenerateFn = Generate,
            ClientB = (Shape, cx, cy, Radius, HalfWidth, HalfHeight, CornerRadius),
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("Sdf"); h.Add(Width); h.Add(Height); h.Add(Shape);
        h.Add(Cx); h.Add(Cy); h.Add(Radius);
        h.Add(HalfWidth); h.Add(HalfHeight); h.Add(CornerRadius);
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var (shape, cx, cy, radius, hw, hh, cornerR) =
            ((VipsSdfShape, double, double, double, double, double, double))b!;
        VipsRect r = outRegion.Valid;
        for (int y = 0; y < r.Height; y++)
        {
            double dy = (r.Top + y) - cy;
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                double dx = (r.Left + x) - cx;
                float v = shape switch
                {
                    VipsSdfShape.Circle => (float)(Math.Sqrt(dx * dx + dy * dy) - radius),
                    VipsSdfShape.Box => (float)BoxSdf(dx, dy, hw, hh),
                    VipsSdfShape.RoundedBox => (float)(BoxSdf(dx, dy, hw - cornerR, hh - cornerR) - cornerR),
                    _ => 0,
                };
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(x * 4, 4), v);
            }
        }
        return 0;
    }

    /// <summary>SDF for an axis-aligned box of half-extents (hw, hh). Standard formula.</summary>
    private static double BoxSdf(double dx, double dy, double hw, double hh)
    {
        double qx = Math.Abs(dx) - hw;
        double qy = Math.Abs(dy) - hh;
        double outside = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0));
        double inside = Math.Min(Math.Max(qx, qy), 0);
        return outside + inside;
    }
}
