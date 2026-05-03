using System;
using System.Buffers.Binary;
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
    /// <summary>Sign: −1 / 0 / +1. UChar: 0→0, anything else→255.</summary>
    Sign = 10,
    Floor = 11,
    Ceil = 12,
    /// <summary>Round to nearest integer (banker's rounding for ties).</summary>
    Rint = 13,
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

        if (@in.BandFormat == VipsBandFormat.Float)
            return GenerateFloat(inRegion, outRegion, @in, op, operand, r);

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
                // For UChar inputs ≥ 0, the rounding ops are no-ops on the
                // [0, 1] mapped value but useful semantically once we cast
                // from Float.
                VipsMathOperation.Sign => i == 0 ? 0 : 1,
                VipsMathOperation.Floor => x,
                VipsMathOperation.Ceil => x,
                VipsMathOperation.Rint => x,
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

    private static int GenerateFloat(VipsRegion inRegion, VipsRegion outRegion, VipsImage @in, VipsMathOperation op, double operand, VipsRect r)
    {
        // Float input is treated as a raw mathematical value: trig functions
        // take the input as radians directly (not the UChar wrap-into-[0,2π]
        // convention); log/exp/pow apply with their natural semantics; no
        // clamp on output. Matches libvips vips_math.
        int bands = @in.Bands;
        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    int off = (x * bands + bnd) * 4;
                    double v = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(off, 4));
                    double y2 = op switch
                    {
                        VipsMathOperation.Abs => Math.Abs(v),
                        VipsMathOperation.Sin => Math.Sin(v),
                        VipsMathOperation.Cos => Math.Cos(v),
                        VipsMathOperation.Tan => Math.Tan(v),
                        VipsMathOperation.Log => Math.Log(v),
                        VipsMathOperation.Log10 => Math.Log10(v),
                        VipsMathOperation.Exp => Math.Exp(v),
                        VipsMathOperation.Exp10 => Math.Pow(10, v),
                        VipsMathOperation.Sqrt => Math.Sqrt(v),
                        VipsMathOperation.Pow => Math.Pow(v, operand),
                        VipsMathOperation.Sign => Math.Sign(v),
                        VipsMathOperation.Floor => Math.Floor(v),
                        VipsMathOperation.Ceil => Math.Ceiling(v),
                        VipsMathOperation.Rint => Math.Round(v),
                        _ => v
                    };
                    BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(off, 4), (float)y2);
                }
            }
        }
        return 0;
    }
}
