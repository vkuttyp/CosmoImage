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

/// <summary>
/// How <see cref="VipsImageBrush"/> samples beyond the source image's
/// bounds. Mirrors ImageSharp's <c>ImageBrush</c> tiling modes.
/// </summary>
public enum VipsBrushTiling
{
    /// <summary>Sample outside source = clamp to nearest edge pixel.</summary>
    Clamp = 0,
    /// <summary>Sample outside source = wrap around (modulo source size).</summary>
    Repeat = 1,
    /// <summary>Sample outside source = reflect at edges, then wrap.</summary>
    Mirror = 2,
}

/// <summary>
/// Brush that samples colour from a source image. Mirrors ImageSharp's
/// <c>ImageBrush(image)</c>. The source is materialised at
/// construction so per-pixel sampling is just a buffer lookup.
///
/// <para><see cref="OffsetX"/> / <see cref="OffsetY"/> shift the
/// source's origin in destination coordinates: a pixel painted at
/// (offsetX, offsetY) reads source pixel (0, 0). <see cref="Tiling"/>
/// chooses what happens outside the source bounds — clamp, repeat,
/// or mirror.</para>
///
/// <para>UChar source only. Source bands &lt; destination bands
/// behave like the other brushes: only the source's bands are
/// written, leaving any extra destination bands (typically alpha)
/// untouched.</para>
/// </summary>
public sealed class VipsImageBrush : IVipsBrush
{
    private readonly byte[] _pixels;
    private readonly int _w, _h, _pelSize;
    private readonly int _offsetX, _offsetY;
    private readonly VipsBrushTiling _tiling;

    public VipsImageBrush(VipsImage source,
        int offsetX = 0, int offsetY = 0,
        VipsBrushTiling tiling = VipsBrushTiling.Clamp)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (source.BandFormat != VipsBandFormat.UChar)
            throw new ArgumentException("ImageBrush source must be UChar");
        if (source.Pixels is { } existing) _pixels = existing;
        else
        {
            var sink = new MemorySink(source);
            sink.RunAsync().GetAwaiter().GetResult();
            _pixels = sink.Pixels;
        }
        _w = source.Width;
        _h = source.Height;
        _pelSize = source.SizeOfPel;
        _offsetX = offsetX;
        _offsetY = offsetY;
        _tiling = tiling;
    }

    public void SampleAt(int x, int y, Span<byte> dst)
    {
        int sx = x - _offsetX;
        int sy = y - _offsetY;
        sx = MapCoord(sx, _w, _tiling);
        sy = MapCoord(sy, _h, _tiling);
        if (sx < 0 || sy < 0) return; // degenerate empty source
        int srcOff = (sy * _w + sx) * _pelSize;
        int n = Math.Min(_pelSize, dst.Length);
        _pixels.AsSpan(srcOff, n).CopyTo(dst);
    }

    private static int MapCoord(int v, int size, VipsBrushTiling tiling)
    {
        if (size <= 0) return -1;
        switch (tiling)
        {
            case VipsBrushTiling.Clamp:
                if (v < 0) return 0;
                if (v >= size) return size - 1;
                return v;
            case VipsBrushTiling.Repeat:
                int mod = v % size;
                return mod < 0 ? mod + size : mod;
            case VipsBrushTiling.Mirror:
                int cycle = size * 2;
                int m = v % cycle;
                if (m < 0) m += cycle;
                return m < size ? m : cycle - 1 - m;
            default:
                return v;
        }
    }
}

/// <summary>
/// Repeating-tile pattern brush. Mirrors ImageSharp's
/// <c>PatternBrush(tile)</c>. Equivalent to
/// <see cref="VipsImageBrush"/> with <see cref="VipsBrushTiling.Repeat"/>.
/// </summary>
public sealed class VipsPatternBrush : IVipsBrush
{
    private readonly VipsImageBrush _inner;

    public VipsPatternBrush(VipsImage tile, int offsetX = 0, int offsetY = 0)
    {
        _inner = new VipsImageBrush(tile, offsetX, offsetY, VipsBrushTiling.Repeat);
    }

    public void SampleAt(int x, int y, Span<byte> dst) => _inner.SampleAt(x, y, dst);
}

/// <summary>
/// Brush that interpolates colour across an N-vertex polygon. Mirrors
/// ImageSharp's <c>PathGradientBrush(points, colours, centerColour)</c>.
///
/// <para>The polygon is fan-triangulated from its centroid; each
/// triangle (centroid, v_i, v_{i+1}) carries barycentric interpolation
/// between the centre colour and the two adjacent vertex colours.
/// Pixels outside the polygon fall back to the centre colour.</para>
///
/// <para>If <c>centerColour</c> is omitted, the per-band average of
/// the vertex colours is used.</para>
/// </summary>
public sealed class VipsPathGradientBrush : IVipsBrush
{
    private readonly (double x, double y)[] _vertices;
    private readonly byte[][] _colors;
    private readonly byte[] _centerColor;
    private readonly double _cx, _cy;

    public VipsPathGradientBrush((double x, double y)[] vertices, byte[][] colors,
        byte[]? centerColor = null)
    {
        if (vertices == null) throw new ArgumentNullException(nameof(vertices));
        if (colors == null) throw new ArgumentNullException(nameof(colors));
        if (vertices.Length < 3) throw new ArgumentException("PathGradient needs ≥ 3 vertices");
        if (colors.Length != vertices.Length)
            throw new ArgumentException("Vertex count and colour count must match");
        int bands = colors[0].Length;
        for (int i = 1; i < colors.Length; i++)
            if (colors[i].Length != bands)
                throw new ArgumentException("All vertex colours must have the same band count");
        if (centerColor != null && centerColor.Length != bands)
            throw new ArgumentException("Centre colour band count must match vertex colours");

        _vertices = ((double x, double y)[])vertices.Clone();
        _colors = new byte[colors.Length][];
        for (int i = 0; i < colors.Length; i++) _colors[i] = (byte[])colors[i].Clone();

        if (centerColor != null) _centerColor = (byte[])centerColor.Clone();
        else
        {
            _centerColor = new byte[bands];
            var sum = new double[bands];
            foreach (var c in colors)
                for (int b = 0; b < bands; b++) sum[b] += c[b];
            for (int b = 0; b < bands; b++)
                _centerColor[b] = (byte)Math.Round(sum[b] / colors.Length);
        }

        double sx = 0, sy = 0;
        foreach (var v in vertices) { sx += v.x; sy += v.y; }
        _cx = sx / vertices.Length;
        _cy = sy / vertices.Length;
    }

    public void SampleAt(int x, int y, Span<byte> dst)
    {
        int n = _vertices.Length;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            double ax = _cx, ay = _cy;
            double bx = _vertices[i].x, by = _vertices[i].y;
            double cx = _vertices[j].x, cy = _vertices[j].y;
            // Barycentric of (x, y) in triangle (a=centroid, b=v_i, c=v_j).
            double denom = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            if (Math.Abs(denom) < 1e-12) continue;
            double u = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) / denom;
            double v = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) / denom;
            double w = 1 - u - v;
            if (u >= -1e-9 && v >= -1e-9 && w >= -1e-9)
            {
                int bands = Math.Min(_centerColor.Length, dst.Length);
                for (int b = 0; b < bands; b++)
                {
                    double cVal = u * _centerColor[b] + v * _colors[i][b] + w * _colors[j][b];
                    dst[b] = (byte)Math.Clamp(Math.Round(cVal), 0, 255);
                }
                return;
            }
        }
        // Outside the polygon — fall back to the centre colour.
        int n2 = Math.Min(_centerColor.Length, dst.Length);
        _centerColor.AsSpan(0, n2).CopyTo(dst);
    }
}
