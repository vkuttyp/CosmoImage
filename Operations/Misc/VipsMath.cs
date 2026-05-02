using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

public enum VipsMathOperation
{
    Abs = 0,
    Sin = 1,
    Cos = 2,
    Tan = 3,
    Log = 4,
    Log10 = 5,
    Exp = 6,
    Exp10 = 7,
    Sqrt = 8,
    Pow = 9,
}

/// <summary>
/// Pointwise math op. Operates on UChar in/out: inputs are interpreted as the
/// fraction <c>x = byte / 255</c>, the function is applied, the result is
/// scaled back to [0,255] and clamped. Trig functions interpret the byte as
/// an angle in radians via <c>x = (byte / 255) * 2π</c>. Mirrors libvips
/// <c>vips_math</c> in spirit; the Float-throughout precise variant is
/// deferred to the architectural lift in <c>TODO_PARITY.md</c>.
/// </summary>
public class VipsMath : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public VipsMathOperation Op { get; set; }
    public double Operand { get; set; } // Used by Pow

    public override int Build()
    {
        if (In == null) return -1;

        Out = new VipsImage
        {
            Width = In.Width,
            Height = In.Height,
            Bands = In.Bands,
            BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding,
            XRes = In.XRes,
            YRes = In.YRes,
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In,
            ClientB = new { Op, Operand }
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        return HashCode.Combine("Math", RuntimeHelpers.GetHashCode(In), Op, Operand);
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        dynamic config = b!;
        VipsMathOperation op = config.Op;
        double operand = config.Operand;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        int totalBytes = r.Width * @in.Bands;
        // Build per-byte LUT once per Generate call. UChar input has only 256
        // distinct values, so we get exact pointwise results without paying
        // 256 transcendental calls per pixel.
        Span<byte> lut = stackalloc byte[256];
        for (int i = 0; i < 256; i++)
        {
            double x = i / 255.0;
            double y = op switch
            {
                VipsMathOperation.Abs => x, // UChar is unsigned, abs is identity
                VipsMathOperation.Sin => Math.Sin(x * 2 * Math.PI),
                VipsMathOperation.Cos => Math.Cos(x * 2 * Math.PI),
                VipsMathOperation.Tan => Math.Tan(x * 2 * Math.PI),
                // log/log10/exp shifted so input 0 → 0 and input 1 → 1
                VipsMathOperation.Log => i == 0 ? 0 : Math.Log(1 + x * (Math.E - 1)),
                VipsMathOperation.Log10 => i == 0 ? 0 : Math.Log10(1 + x * 9),
                VipsMathOperation.Exp => (Math.Exp(x) - 1) / (Math.E - 1),
                VipsMathOperation.Exp10 => (Math.Pow(10, x) - 1) / 9,
                VipsMathOperation.Sqrt => Math.Sqrt(x),
                VipsMathOperation.Pow => Math.Pow(x, operand),
                _ => x
            };
            // Map [-1, 1] (trig) to [0, 255] via (y + 1) / 2; map [0, 1] directly.
            double scaled = op == VipsMathOperation.Sin || op == VipsMathOperation.Cos || op == VipsMathOperation.Tan
                ? (y + 1) * 127.5
                : y * 255.0;
            lut[i] = (byte)Math.Clamp(scaled, 0, 255);
        }

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int i = 0; i < totalBytes; i++)
                outAddr[i] = lut[inAddr[i]];
        }

        return 0;
    }
}
