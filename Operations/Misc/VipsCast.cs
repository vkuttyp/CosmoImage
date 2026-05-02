using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Misc;

/// <summary>
/// Numeric band-format conversion. Mirrors libvips <c>vips_cast</c>: copies
/// each band's value into the target type with no auto-normalization.
/// UChar 100 → Float 100.0 (not 100/255). Callers who want the [0,1] range
/// apply a follow-on <c>Linear(1/255, 0)</c>.
///
/// <para>Currently handles UChar↔Float in both directions; identity casts
/// are pass-through (returns the input unchanged via <see cref="Run"/>).
/// Other format pairs are deferred along with the rest of the
/// Float-throughout work tracked in <c>TODO_PARITY.md</c>.</para>
/// </summary>
public class VipsCast : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public VipsBandFormat TargetFormat { get; set; }

    public override int Build()
    {
        if (In == null) return -1;

        Out = new VipsImage
        {
            Width = In.Width,
            Height = In.Height,
            Bands = In.Bands,
            BandFormat = TargetFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding,
            XRes = In.XRes,
            YRes = In.YRes,
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In,
            ClientB = new { Source = In.BandFormat, Target = TargetFormat }
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Cast", RuntimeHelpers.GetHashCode(In), TargetFormat);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        dynamic config = b!;
        VipsBandFormat src = config.Source;
        VipsBandFormat dst = config.Target;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        int bands = @in.Bands;

        // Only the two paths the rest of the Float-throughout work needs
        // right now. Identity is filtered out at the trampoline level.
        if (src == VipsBandFormat.UChar && dst == VipsBandFormat.Float)
        {
            for (int y = 0; y < r.Height; y++)
            {
                var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
                var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
                for (int x = 0; x < r.Width; x++)
                {
                    for (int bnd = 0; bnd < bands; bnd++)
                    {
                        float v = inAddr[x * bands + bnd];
                        BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice((x * bands + bnd) * 4, 4), v);
                    }
                }
            }
            return 0;
        }
        if (src == VipsBandFormat.Float && dst == VipsBandFormat.UChar)
        {
            for (int y = 0; y < r.Height; y++)
            {
                var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
                var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
                for (int x = 0; x < r.Width; x++)
                {
                    for (int bnd = 0; bnd < bands; bnd++)
                    {
                        float v = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice((x * bands + bnd) * 4, 4));
                        outAddr[x * bands + bnd] = (byte)Math.Clamp(MathF.Round(v), 0, 255);
                    }
                }
            }
            return 0;
        }

        throw new NotSupportedException($"Cast {src} → {dst} not implemented.");
    }
}
