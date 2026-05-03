using System;

namespace CosmoImage.Operations.Drawing;

public enum VipsLineCap
{
    /// <summary>Square cut perpendicular to the line at the endpoint.</summary>
    Butt = 0,
}

public enum VipsLineJoin
{
    /// <summary>Outer corner clipped flat — joins the two stroke edges with a straight bevel.</summary>
    Bevel = 0,
}

/// <summary>
/// Stroke specification: a brush + width + cap / join styles.
/// Mirrors ImageSharp's <c>SolidPen(brush, width)</c>.
///
/// <para>Round 62 ships <see cref="VipsLineCap.Butt"/> and
/// <see cref="VipsLineJoin.Bevel"/> only; miter / round joins, square
/// / round caps, and dash patterns come in later rounds. The bevel
/// join is the cheapest and produces clean output for typical
/// stroke widths and angles.</para>
/// </summary>
public sealed class VipsPen
{
    public IVipsBrush Brush { get; }
    public double Width { get; }
    public VipsLineCap Cap { get; }
    public VipsLineJoin Join { get; }

    public VipsPen(IVipsBrush brush, double width,
        VipsLineCap cap = VipsLineCap.Butt, VipsLineJoin join = VipsLineJoin.Bevel)
    {
        if (width <= 0) throw new ArgumentException("Pen width must be positive");
        Brush = brush ?? throw new ArgumentNullException(nameof(brush));
        Width = width;
        Cap = cap;
        Join = join;
    }

    /// <summary>Convenience: solid-colour pen at the given width.</summary>
    public static VipsPen Solid(byte r, byte g, byte b, double width)
        => new(new VipsSolidBrush(r, g, b), width);

    public static VipsPen Solid(byte r, byte g, byte b, byte a, double width)
        => new(new VipsSolidBrush(r, g, b, a), width);
}
