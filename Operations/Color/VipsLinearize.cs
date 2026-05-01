using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

/// <summary>
/// Convert sRGB-encoded UChar pixels to linear-light values via the IEC
/// 61966-2-1 inverse transfer function (the proper piecewise curve, not the
/// pow(2.2) approximation). Use this before color-sensitive ops like Resize,
/// GaussBlur, or Composite to avoid gamma-encoded blending artifacts (halos
/// at sharp edges, dark fringes around alpha edges). Pair with
/// <see cref="VipsDelinearize"/> at the end of the pipeline.
///
/// Output remains UChar so existing UChar-only ops still work; precision
/// loss in the lower range is the price paid for that compatibility.
/// True high-precision linear-light processing wants Float band format,
/// which most ops in this port don't yet handle.
/// </summary>
public class VipsLinearize : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    private static byte[]? _lut;

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
            ClientA = In
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Linearize", RuntimeHelpers.GetHashCode(In));

    internal static byte[] SrgbToLinearLut()
    {
        if (_lut != null) return _lut;
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double u = i / 255.0;
            double linear = u <= 0.04045
                ? u / 12.92
                : Math.Pow((u + 0.055) / 1.055, 2.4);
            lut[i] = (byte)Math.Clamp(linear * 255.0, 0, 255);
        }
        _lut = lut;
        return lut;
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        VipsRect r = outRegion.Valid;
        if (inRegion.Prepare(r) != 0) return -1;

        var lut = SrgbToLinearLut();
        int bands = @in.Bands;
        int pelSize = @in.SizeOfPel;
        bool hasAlpha = bands == 2 || bands == 4;
        int colorBands = hasAlpha ? bands - 1 : bands;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int o = x * pelSize;
                for (int i = 0; i < colorBands; i++)
                    outAddr[o + i] = lut[inAddr[o + i]];
                if (hasAlpha) outAddr[o + colorBands] = inAddr[o + colorBands];
            }
        }
        return 0;
    }
}

/// <summary>
/// Inverse of <see cref="VipsLinearize"/>: convert linear-light UChar pixels
/// back to sRGB encoding using the IEC 61966-2-1 forward transfer function.
/// Apply at the end of a linear-light pipeline so output displays correctly
/// in sRGB-aware viewers.
/// </summary>
public class VipsDelinearize : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

    private static byte[]? _lut;

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
            ClientA = In
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Delinearize", RuntimeHelpers.GetHashCode(In));

    internal static byte[] LinearToSrgbLut()
    {
        if (_lut != null) return _lut;
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double linear = i / 255.0;
            double u = linear <= 0.0031308
                ? linear * 12.92
                : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
            lut[i] = (byte)Math.Clamp(u * 255.0, 0, 255);
        }
        _lut = lut;
        return lut;
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        VipsRect r = outRegion.Valid;
        if (inRegion.Prepare(r) != 0) return -1;

        var lut = LinearToSrgbLut();
        int bands = @in.Bands;
        int pelSize = @in.SizeOfPel;
        bool hasAlpha = bands == 2 || bands == 4;
        int colorBands = hasAlpha ? bands - 1 : bands;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                int o = x * pelSize;
                for (int i = 0; i < colorBands; i++)
                    outAddr[o + i] = lut[inAddr[o + i]];
                if (hasAlpha) outAddr[o + colorBands] = inAddr[o + colorBands];
            }
        }
        return 0;
    }
}
