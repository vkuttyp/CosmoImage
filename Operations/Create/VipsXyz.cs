using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// Synthesise a 2-band <see cref="VipsBandFormat.UInt"/> image where
/// each pixel value equals its (x, y) coordinate. Useful as a base
/// for warp/remap test fixtures, ramps, and visualising spatial
/// relationships. Mirrors libvips <c>vips_xyz</c>.
///
/// <para>The optional <see cref="Csize"/> / <see cref="Dsize"/> /
/// <see cref="Esize"/> parameters add additional dimensions to the
/// output (3-band, 4-band, 5-band) for higher-dimensional coordinate
/// images, à la libvips' n-D extension.</para>
/// </summary>
public class VipsXyz : VipsOperation
{
    public VipsImage? Out { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Optional 3rd-dim size (rolls Z into the band axis when &gt; 1).</summary>
    public int Csize { get; set; } = 1;
    public int Dsize { get; set; } = 1;
    public int Esize { get; set; } = 1;

    public override int Build()
    {
        if (Width < 1 || Height < 1) return -1;
        if (Csize < 1 || Dsize < 1 || Esize < 1) return -1;
        // Bands = 2 + (Csize > 1 ? 1 : 0) + (Dsize > 1 ? 1 : 0) + (Esize > 1 ? 1 : 0)
        int bands = 2;
        if (Csize > 1) bands++;
        if (Dsize > 1) bands++;
        if (Esize > 1) bands++;

        Out = new VipsImage
        {
            Width = Width, Height = Height * Csize * Dsize * Esize, Bands = bands,
            BandFormat = VipsBandFormat.UInt,
            Interpretation = VipsInterpretation.Multiband,
            XRes = 1, YRes = 1,
            GenerateFn = Generate,
            ClientB = (Width, Height, Csize, Dsize, Esize),
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Xyz", Width, Height, Csize, Dsize, Esize);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var (W, H, C, D, E) = ((int, int, int, int, int))b!;
        VipsRect r = outRegion.Valid;
        int outBands = outRegion.Image.Bands;

        for (int y = 0; y < r.Height; y++)
        {
            int gy = r.Top + y;
            // Decompose gy into (e, d, c, y') = (slowest..fastest).
            int rest = gy;
            int yi = rest % H; rest /= H;
            int ci = rest % C; rest /= C;
            int di = rest % D; rest /= D;
            int ei = rest;

            var outAddr = outRegion.GetAddress(r.Left, gy);
            for (int x = 0; x < r.Width; x++)
            {
                int gx = r.Left + x;
                int off = x * outBands * 4;
                WriteUInt(outAddr.Slice(off, 4), (uint)gx);
                WriteUInt(outAddr.Slice(off + 4, 4), (uint)yi);
                int bnd = 2;
                if (C > 1) WriteUInt(outAddr.Slice(off + bnd++ * 4, 4), (uint)ci);
                if (D > 1) WriteUInt(outAddr.Slice(off + bnd++ * 4, 4), (uint)di);
                if (E > 1) WriteUInt(outAddr.Slice(off + bnd++ * 4, 4), (uint)ei);
            }
        }
        return 0;
    }

    private static void WriteUInt(Span<byte> span, uint v)
    {
        span[0] = (byte)v;
        span[1] = (byte)(v >> 8);
        span[2] = (byte)(v >> 16);
        span[3] = (byte)(v >> 24);
    }
}
