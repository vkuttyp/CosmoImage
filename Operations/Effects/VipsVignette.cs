using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Effects;

/// <summary>
/// Darken the corners of the image with a smooth radial falloff. <c>Strength</c>
/// in 0..1 controls how much the corners darken (0 = no effect, 1 = corners
/// black). Quadratic falloff: factor = 1 - strength * (r/R)^2 where r is the
/// pixel's distance from image center and R is the half-diagonal.
/// </summary>
public class VipsVignette : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    /// <summary>Vignette intensity 0..1.</summary>
    public double Strength { get; set; } = 0.5;

    public override int Build()
    {
        if (In == null) return -1;
        if (Strength < 0 || Strength > 1) return -1;

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
            ClientB = new { Strength, Cx = In.Width * 0.5, Cy = In.Height * 0.5,
                            InvR2 = 1.0 / (In.Width * In.Width * 0.25 + In.Height * In.Height * 0.25) }
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Vignette", RuntimeHelpers.GetHashCode(In), Strength);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        dynamic config = b!;
        double strength = config.Strength;
        double cx = config.Cx;
        double cy = config.Cy;
        double invR2 = config.InvR2;
        VipsRect r = outRegion.Valid;
        if (inRegion.Prepare(r) != 0) return -1;

        int bands = @in.Bands;
        bool hasAlpha = bands == 2 || bands == 4;
        int colorBands = hasAlpha ? bands - 1 : bands;

        if (@in.BandFormat == VipsBandFormat.Float)
            return GenerateFloat(inRegion, outRegion, r, strength, cx, cy, invR2, bands, colorBands, hasAlpha);

        int pelSize = @in.SizeOfPel;

        for (int y = 0; y < r.Height; y++)
        {
            int gy = r.Top + y;
            double dy = gy - cy;
            double dy2 = dy * dy;

            var inAddr = inRegion.GetAddress(r.Left, gy);
            var outAddr = outRegion.GetAddress(r.Left, gy);

            for (int x = 0; x < r.Width; x++)
            {
                int gx = r.Left + x;
                double dx = gx - cx;
                double r2norm = (dx * dx + dy2) * invR2;
                if (r2norm > 1.0) r2norm = 1.0;
                double factor = 1.0 - strength * r2norm;

                int o = x * pelSize;
                for (int i = 0; i < colorBands; i++)
                    outAddr[o + i] = (byte)Math.Clamp(inAddr[o + i] * factor, 0, 255);
                if (hasAlpha) outAddr[o + colorBands] = inAddr[o + colorBands];
            }
        }
        return 0;
    }

    private static int GenerateFloat(VipsRegion inRegion, VipsRegion outRegion, VipsRect r,
        double strength, double cx, double cy, double invR2, int bands, int colorBands, bool hasAlpha)
    {
        for (int y = 0; y < r.Height; y++)
        {
            int gy = r.Top + y;
            double dy = gy - cy;
            double dy2 = dy * dy;

            var inAddr = inRegion.GetAddress(r.Left, gy);
            var outAddr = outRegion.GetAddress(r.Left, gy);

            for (int x = 0; x < r.Width; x++)
            {
                int gx = r.Left + x;
                double dx = gx - cx;
                double r2norm = (dx * dx + dy2) * invR2;
                if (r2norm > 1.0) r2norm = 1.0;
                double factor = 1.0 - strength * r2norm;

                int baseIdx = x * bands * 4;
                for (int i = 0; i < colorBands; i++)
                {
                    int off = baseIdx + i * 4;
                    float v = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(off, 4));
                    BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(off, 4), (float)(v * factor));
                }
                if (hasAlpha)
                {
                    inAddr.Slice(baseIdx + colorBands * 4, 4)
                          .CopyTo(outAddr.Slice(baseIdx + colorBands * 4, 4));
                }
            }
        }
        return 0;
    }
}
