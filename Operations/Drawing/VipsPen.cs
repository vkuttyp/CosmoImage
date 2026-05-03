using System;

namespace CosmoImage.Operations.Drawing;

public enum VipsLineCap
{
    /// <summary>Perpendicular cut at the endpoint (no extension).</summary>
    Butt = 0,
    /// <summary>Endpoint extended by <c>width/2</c> along the segment direction, then perpendicular cut.</summary>
    Square = 1,
    /// <summary>Half-circle of radius <c>width/2</c> at the endpoint.</summary>
    Round = 2,
}

public enum VipsLineJoin
{
    /// <summary>Outer corner clipped flat — joins the two stroke edges with a straight bevel.</summary>
    Bevel = 0,
    /// <summary>Outlines extended to their outer intersection. Falls back to bevel when the join exceeds <see cref="VipsPen.MiterLimit"/>.</summary>
    Miter = 1,
    /// <summary>Quarter-circle arc of radius <c>width/2</c> centred at the join vertex.</summary>
    Round = 2,
}

/// <summary>
/// Stroke specification: a brush + width + cap / join styles +
/// optional dash pattern. Mirrors ImageSharp's
/// <c>SolidPen(brush, width)</c> / <c>Pen(brush, width, dashes)</c>.
///
/// <para>Round 64 added all three join styles (bevel / miter / round)
/// and all three cap styles (butt / square / round). Miter joins
/// fall back to bevel when the outer-intersection distance exceeds
/// <c>MiterLimit · width/2</c> (default 4 — matches CSS / SVG).</para>
///
/// <para>Round 65 added <see cref="Dashes"/> — alternating on/off
/// arc-lengths cycled along the path. <see cref="DashOffset"/>
/// shifts the starting phase. Each "on" interval becomes its own
/// sub-path with its own caps at the dash boundaries.</para>
/// </summary>
public sealed class VipsPen
{
    public IVipsBrush Brush { get; }
    public double Width { get; }
    public VipsLineCap Cap { get; }
    public VipsLineJoin Join { get; }
    /// <summary>Miter-spike cap, in units of <c>width/2</c>. Default 4.</summary>
    public double MiterLimit { get; }
    /// <summary>
    /// Alternating on/off arc-lengths. <c>null</c> = solid (no dash).
    /// First entry is the first "on" length; second the first "off";
    /// pattern repeats. Must contain at least one positive value.
    /// </summary>
    public double[]? Dashes { get; }
    /// <summary>Starting phase along the dash cycle, in arc-length.</summary>
    public double DashOffset { get; }

    public VipsPen(IVipsBrush brush, double width,
        VipsLineCap cap = VipsLineCap.Butt,
        VipsLineJoin join = VipsLineJoin.Bevel,
        double miterLimit = 4.0,
        double[]? dashes = null,
        double dashOffset = 0)
    {
        if (width <= 0) throw new ArgumentException("Pen width must be positive");
        if (miterLimit < 1.0) throw new ArgumentException("Miter limit must be ≥ 1");
        if (dashes != null)
        {
            if (dashes.Length == 0) throw new ArgumentException("Dashes array must not be empty");
            double total = 0;
            foreach (var d in dashes)
            {
                if (d < 0) throw new ArgumentException("Dash lengths must be non-negative");
                total += d;
            }
            if (total <= 0) throw new ArgumentException("At least one dash length must be positive");
        }
        Brush = brush ?? throw new ArgumentNullException(nameof(brush));
        Width = width;
        Cap = cap;
        Join = join;
        MiterLimit = miterLimit;
        Dashes = dashes != null ? (double[])dashes.Clone() : null;
        DashOffset = dashOffset;
    }

    /// <summary>Convenience: solid-colour pen at the given width.</summary>
    public static VipsPen Solid(byte r, byte g, byte b, double width)
        => new(new VipsSolidBrush(r, g, b), width);

    public static VipsPen Solid(byte r, byte g, byte b, byte a, double width)
        => new(new VipsSolidBrush(r, g, b, a), width);

    /// <summary>Convenience: dashed solid-colour pen.</summary>
    public static VipsPen Dashed(byte r, byte g, byte b, double width,
        params double[] dashes)
        => new(new VipsSolidBrush(r, g, b), width, dashes: dashes);
}
