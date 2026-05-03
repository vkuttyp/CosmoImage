using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// Synthesise a zone-plate test image: <c>cos(r²)</c> where r is the
/// distance from the image centre, scaled so the highest frequencies
/// land at the corners. Mirrors libvips <c>vips_zone</c>. Float
/// single-band output in <c>[-1, 1]</c>.
///
/// <para>The canonical resize/anti-alias diagnostic. A correctly
/// downsampled zone plate stays as smooth concentric rings; an
/// aliased downsample produces dramatic Moiré spirals. Pair with
/// <c>Resize</c> at varying kernels to compare interpolators
/// visually.</para>
/// </summary>
public class VipsZone : VipsOperation
{
    public VipsImage? Out { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public override int Build()
    {
        if (Width < 1 || Height < 1) return -1;

        Out = new VipsImage
        {
            Width = Width, Height = Height, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.BW,
            XRes = 1, YRes = 1,
            GenerateFn = Generate, ClientB = (Width, Height),
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Zone", Width, Height);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var (W, H) = ((int, int))b!;
        VipsRect r = outRegion.Valid;
        double cx = W / 2.0, cy = H / 2.0;
        // Scale so half-amplitude r² gives π at the image diagonal — i.e.,
        // Nyquist frequency at the edges.
        double scale = Math.PI / (Math.Min(W, H) / 2.0);

        for (int y = 0; y < r.Height; y++)
        {
            double dy = (r.Top + y) - cy;
            double dy2 = dy * dy;
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                double dx = (r.Left + x) - cx;
                double r2 = dx * dx + dy2;
                float v = (float)Math.Cos(r2 * scale);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(x * 4, 4), v);
            }
        }
        return 0;
    }
}
