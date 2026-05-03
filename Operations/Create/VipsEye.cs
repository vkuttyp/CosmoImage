using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// Synthesise an "eye-test" pattern: a horizontal frequency chirp with
/// vertical amplitude ramp. Frequency rises from zero on the left to
/// <see cref="Factor"/> cycles per pixel on the right; amplitude
/// climbs from zero on the bottom row to one on the top. Mirrors
/// libvips <c>vips_eye</c>. Float single-band output.
///
/// <para>Use in resize-aliasing diagnostics — downsampled output of
/// an eye test should remain a smooth horizontal-frequency band, not
/// a moiré herringbone. The vertical axis lets you see the
/// frequency-vs-contrast detection threshold (the contrast-sensitivity
/// "blob") familiar from textbook visual-system charts.</para>
/// </summary>
public class VipsEye : VipsOperation
{
    public VipsImage? Out { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Maximum spatial frequency at the right edge, in cycles / pixel.</summary>
    public double Factor { get; set; } = 0.5;

    public override int Build()
    {
        if (Width < 1 || Height < 1) return -1;

        Out = new VipsImage
        {
            Width = Width, Height = Height, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.BW,
            XRes = 1, YRes = 1,
            GenerateFn = Generate, ClientB = (Width, Height, Factor),
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Eye", Width, Height, Factor);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var (W, H, factor) = ((int, int, double))b!;
        VipsRect r = outRegion.Valid;
        for (int y = 0; y < r.Height; y++)
        {
            int gy = r.Top + y;
            // Amplitude ramps top-to-bottom: top row = 1, bottom row = 0.
            double amp = 1.0 - gy / (double)(H - 1);
            var outAddr = outRegion.GetAddress(r.Left, gy);
            for (int x = 0; x < r.Width; x++)
            {
                int gx = r.Left + x;
                // Phase grows as gx² so frequency rises linearly with x.
                double phase = Math.PI * factor * gx * gx / W;
                float v = (float)(amp * Math.Cos(phase));
                BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(x * 4, 4), v);
            }
        }
        return 0;
    }
}
