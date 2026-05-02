using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using CosmoImage.Operations.Analysis;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Linear stretch the input to <c>0..255</c> UChar. The classic use is
/// turning a Float image (e.g. an FFT magnitude or a depth map) into
/// something visually displayable. Mirrors libvips <c>vips_scale</c>.
///
/// <para>Two modes:</para>
/// <list type="bullet">
/// <item><c>Log = false</c> (default): map <c>min..max</c> to
///   <c>0..255</c>. The min/max are computed across all bands together.
///   </item>
/// <item><c>Log = true</c>: log-scale, useful for highly skewed
///   distributions (FFT magnitudes especially). Maps
///   <c>log(1 + |x|)/log(1 + max)</c> to <c>0..255</c>.</item>
/// </list>
///
/// <para>Stats are computed eagerly at <c>Build</c> time — <c>scale</c>
/// is a global operation, so we materialise once and the streaming
/// downstream can scan the result lazily.</para>
/// </summary>
public class VipsScale : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public bool Log { get; set; }
    /// <summary>Log-scale exponent; libvips defaults to 0.25.</summary>
    public double Exponent { get; set; } = 0.25;

    public override int Build()
    {
        if (In == null) return -1;

        var stats = VipsStats.Compute(In);
        // Stats arrays are length Bands+1 with the last entry being the aggregate.
        int agg = In.Bands;
        double min = stats.Min[agg];
        double max = stats.Max[agg];

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = In.Bands,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = In.Bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = (min, max, Log, Exponent),
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Scale", RuntimeHelpers.GetHashCode(In), Log, Exponent);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        var (min, max, log, exp) = ((double, double, bool, double))b!;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        int bands = @in.Bands;
        bool isFloat = @in.BandFormat == VipsBandFormat.Float;
        int sampleSize = isFloat ? 4 : 1;
        int pelBytes = bands * sampleSize;

        // Precompute scale factor — degenerate-input guard.
        double range = max - min;
        double linScale = range > 0 ? 255.0 / range : 0.0;
        double logMax = Math.Log(1 + Math.Pow(Math.Abs(max), exp));
        double logScale = logMax > 0 ? 255.0 / logMax : 0.0;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    double v = isFloat
                        ? BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice((x * bands + bnd) * 4, 4))
                        : inAddr[x * bands + bnd];

                    double mapped = log
                        ? Math.Log(1 + Math.Pow(Math.Abs(v), exp)) * logScale
                        : (v - min) * linScale;

                    outAddr[x * bands + bnd] = (byte)Math.Clamp(mapped, 0, 255);
                }
            }
        }
        return 0;
    }
}
