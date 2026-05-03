using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Drawing;

/// <summary>
/// Fill the entire image with a constant <see cref="Color"/>.
/// Mirrors ImageSharp's <c>Clear(color)</c> processor — the standard
/// "wipe to background" op for canvas reset.
///
/// <para>Same dimensions / bands / band-format as the input; per-band
/// colour applied uniformly. UChar and Float branches.</para>
/// </summary>
public class VipsClear : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    /// <summary>Per-band fill colour. Length should equal input's band count.</summary>
    public double[]? Color { get; set; }

    public override int Build()
    {
        if (In == null || Color == null) return -1;
        if (Color.Length != In.Bands) return -1;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = In.Bands, BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = Color,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("Clear"); h.Add(RuntimeHelpers.GetHashCode(In));
        if (Color != null) foreach (var c in Color) h.Add(c);
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var color = (double[])b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        bool isFloat = @out.BandFormat == VipsBandFormat.Float;
        int bands = @out.Bands;
        int sampleSize = isFloat ? 4 : 1;
        int pelBytes = bands * sampleSize;

        // Build a single packed pel; then memcpy the row by repeating it.
        var pel = new byte[pelBytes];
        if (isFloat)
        {
            for (int bnd = 0; bnd < bands; bnd++)
                BinaryPrimitives.WriteSingleLittleEndian(pel.AsSpan(bnd * 4, 4), (float)color[bnd]);
        }
        else
        {
            for (int bnd = 0; bnd < bands; bnd++)
                pel[bnd] = (byte)Math.Clamp(color[bnd], 0, 255);
        }

        for (int y = 0; y < r.Height; y++)
        {
            var addr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
                pel.AsSpan().CopyTo(addr.Slice(x * pelBytes, pelBytes));
        }
        return 0;
    }
}
