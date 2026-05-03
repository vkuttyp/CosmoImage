using System;

namespace CosmoImage.Operations.Drawing;

/// <summary>
/// Brush — a per-pixel colour source for fill / stroke ops. Sampled
/// at every painted pixel; the brush returns the per-band colour to
/// write to that position. Mirrors ImageSharp's <c>IBrush</c>.
///
/// <para>Brush implementations include <see cref="VipsSolidBrush"/>
/// (constant colour), and the gradient / image / pattern brushes
/// shipped in later rounds.</para>
/// </summary>
public interface IVipsBrush
{
    /// <summary>
    /// Sample the brush at output pixel <c>(x, y)</c>. Output bytes
    /// are written in the target image's band order; length must
    /// match <paramref name="dst"/>'s capacity (= image's
    /// <c>SizeOfPel</c>).
    /// </summary>
    void SampleAt(int x, int y, Span<byte> dst);
}

/// <summary>
/// Constant-colour brush. The simplest <see cref="IVipsBrush"/>:
/// every sample returns the same per-band colour. Mirrors
/// ImageSharp's <c>SolidBrush(Color)</c>.
/// </summary>
public sealed class VipsSolidBrush : IVipsBrush
{
    private readonly byte[] _color;

    public VipsSolidBrush(params byte[] color) { _color = (byte[])color.Clone(); }

    public void SampleAt(int x, int y, Span<byte> dst)
    {
        // If the brush colour is shorter than the destination pel
        // (e.g. RGB brush over RGBA image), broadcast: copy what we
        // have, leave trailing alpha at whatever the caller already
        // wrote. Callers for FillPath pre-fill with the alpha byte.
        int n = Math.Min(_color.Length, dst.Length);
        _color.AsSpan(0, n).CopyTo(dst);
    }
}

/// <summary>
/// Linear gradient: colour interpolates linearly along the line
/// from <c>(X0, Y0)</c> to <c>(X1, Y1)</c>. Pixels project onto that
/// line; their distance along it (clamped to <c>[0, 1]</c>) drives
/// a lerp between <see cref="ColorStart"/> and <see cref="ColorEnd"/>.
/// </summary>
public sealed class VipsLinearGradientBrush : IVipsBrush
{
    private readonly double _x0, _y0;
    private readonly double _dx, _dy;
    private readonly double _lenSq;
    private readonly byte[] _start;
    private readonly byte[] _end;

    public VipsLinearGradientBrush(double x0, double y0, double x1, double y1,
        byte[] colorStart, byte[] colorEnd)
    {
        if (colorStart.Length != colorEnd.Length)
            throw new ArgumentException("Gradient colours must have matching band counts");
        _x0 = x0; _y0 = y0;
        _dx = x1 - x0; _dy = y1 - y0;
        _lenSq = _dx * _dx + _dy * _dy;
        _start = (byte[])colorStart.Clone();
        _end = (byte[])colorEnd.Clone();
    }

    public void SampleAt(int x, int y, Span<byte> dst)
    {
        double t = _lenSq > 0
            ? ((x - _x0) * _dx + (y - _y0) * _dy) / _lenSq
            : 0;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        int n = Math.Min(_start.Length, dst.Length);
        for (int i = 0; i < n; i++)
            dst[i] = (byte)Math.Round(_start[i] + (_end[i] - _start[i]) * t);
    }
}

/// <summary>
/// Radial gradient: colour interpolates from <see cref="ColorCentre"/>
/// at <c>(Cx, Cy)</c> to <see cref="ColorEdge"/> at distance
/// <see cref="Radius"/>. Pixels beyond <c>Radius</c> clamp to the
/// edge colour.
/// </summary>
public sealed class VipsRadialGradientBrush : IVipsBrush
{
    private readonly double _cx, _cy, _radius;
    private readonly byte[] _centre;
    private readonly byte[] _edge;

    public VipsRadialGradientBrush(double cx, double cy, double radius,
        byte[] colorCentre, byte[] colorEdge)
    {
        if (colorCentre.Length != colorEdge.Length)
            throw new ArgumentException("Gradient colours must have matching band counts");
        if (radius <= 0) throw new ArgumentException("Radius must be positive");
        _cx = cx; _cy = cy; _radius = radius;
        _centre = (byte[])colorCentre.Clone();
        _edge = (byte[])colorEdge.Clone();
    }

    public void SampleAt(int x, int y, Span<byte> dst)
    {
        double dx = x - _cx, dy = y - _cy;
        double t = Math.Sqrt(dx * dx + dy * dy) / _radius;
        if (t > 1) t = 1;
        int n = Math.Min(_centre.Length, dst.Length);
        for (int i = 0; i < n; i++)
            dst[i] = (byte)Math.Round(_centre[i] + (_edge[i] - _centre[i]) * t);
    }
}
