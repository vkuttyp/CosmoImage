using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// Synthesise a 2D sinusoid pattern. Each pixel is
/// <c>sin(2π · (x · hfreq / W + y · vfreq / H))</c> — frequencies in
/// cycles per image. Mirrors libvips <c>vips_sines</c>. Float
/// single-band output in <c>[-1, 1]</c>.
///
/// <para>The classic stress-test for resize-aliasing diagnosis: a high
/// horizontal frequency input downsampled to a small canvas should
/// remain a smooth low-frequency band, not a moiré pattern. Combine
/// with <c>Cast(UChar)</c> + offset-bias for a viewable image:
/// <c>sines * 127 + 128</c>.</para>
/// </summary>
public class VipsSines : VipsOperation
{
    public VipsImage? Out { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double HFreq { get; set; } = 0.5;
    public double VFreq { get; set; } = 0.5;

    public override int Build()
    {
        if (Width < 1 || Height < 1) return -1;

        Out = new VipsImage
        {
            Width = Width, Height = Height, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.BW,
            XRes = 1, YRes = 1,
            GenerateFn = Generate, ClientB = (Width, Height, HFreq, VFreq),
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Sines", Width, Height, HFreq, VFreq);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var (W, H, hf, vf) = ((int, int, double, double))b!;
        VipsRect r = outRegion.Valid;
        double dx = 2 * Math.PI * hf / W;
        double dy = 2 * Math.PI * vf / H;

        for (int y = 0; y < r.Height; y++)
        {
            double phaseY = (r.Top + y) * dy;
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                float v = (float)Math.Sin((r.Left + x) * dx + phaseY);
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(x * 4, 4), v);
            }
        }
        return 0;
    }
}
