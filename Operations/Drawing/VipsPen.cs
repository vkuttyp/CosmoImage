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
/// Stroke specification: a brush + width + cap / join styles.
/// Mirrors ImageSharp's <c>SolidPen(brush, width)</c>.
///
/// <para>Round 64 ships all three join styles (bevel / miter / round)
/// and all three cap styles (butt / square / round). Miter joins
/// fall back to bevel when the outer-intersection distance exceeds
/// <c>MiterLimit · width/2</c> — the standard Postscript / SVG
/// guard against absurdly long miter spikes at sharp angles.
/// Default miter limit is 4 (matches CSS / SVG defaults).</para>
///
/// <para>Round-cap and round-join arcs are approximated with line
/// segments — the rasteriser doesn't natively understand arcs, so
/// we tessellate them at fill time. ~16 segments per half-turn
/// yields visually-smooth output at typical stroke widths.</para>
/// </summary>
public sealed class VipsPen
{
    public IVipsBrush Brush { get; }
    public double Width { get; }
    public VipsLineCap Cap { get; }
    public VipsLineJoin Join { get; }
    /// <summary>Miter-spike cap, in units of <c>width/2</c>. Default 4.</summary>
    public double MiterLimit { get; }

    public VipsPen(IVipsBrush brush, double width,
        VipsLineCap cap = VipsLineCap.Butt,
        VipsLineJoin join = VipsLineJoin.Bevel,
        double miterLimit = 4.0)
    {
        if (width <= 0) throw new ArgumentException("Pen width must be positive");
        if (miterLimit < 1.0) throw new ArgumentException("Miter limit must be ≥ 1");
        Brush = brush ?? throw new ArgumentNullException(nameof(brush));
        Width = width;
        Cap = cap;
        Join = join;
        MiterLimit = miterLimit;
    }

    /// <summary>Convenience: solid-colour pen at the given width.</summary>
    public static VipsPen Solid(byte r, byte g, byte b, double width)
        => new(new VipsSolidBrush(r, g, b), width);

    public static VipsPen Solid(byte r, byte g, byte b, byte a, double width)
        => new(new VipsSolidBrush(r, g, b, a), width);
}
