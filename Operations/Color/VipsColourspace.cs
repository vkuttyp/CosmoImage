using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Color;

public class VipsColourspace : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public VipsInterpretation Space { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (In.Interpretation == Space)
        {
            Out = In;
            return 0;
        }

        int bands = Space switch
        {
            VipsInterpretation.BW => 1,
            VipsInterpretation.SRGB or VipsInterpretation.RGB => 3,
            VipsInterpretation.scRGB or VipsInterpretation.XYZ or VipsInterpretation.Lab => 3,
            VipsInterpretation.CMYK => 4,
            _ => In.Bands
        };

        VipsBandFormat format = Space switch
        {
            VipsInterpretation.scRGB or VipsInterpretation.XYZ or VipsInterpretation.Lab => VipsBandFormat.Float,
            _ => VipsBandFormat.UChar
        };

        Out = new VipsImage
        {
            Width = In.Width,
            Height = In.Height,
            Bands = bands,
            BandFormat = format,
            Interpretation = Space,
            Coding = In.Coding,
            XRes = In.XRes,
            YRes = In.YRes,
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In,
            ClientB = Space
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        return HashCode.Combine("Colourspace", RuntimeHelpers.GetHashCode(In), Space);
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        VipsInterpretation space = (VipsInterpretation)b!;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        int inBands = @in.Bands;
        int outBands = outRegion.Image.Bands;
        int inPelSize = @in.SizeOfPel;
        int outPelSize = outRegion.Image.SizeOfPel;

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);

            for (int x = 0; x < r.Width; x++)
            {
                var inPix = inAddr.Slice(x * inPelSize, inPelSize);
                var outPix = outAddr.Slice(x * outPelSize, outPelSize);

                double[] rgb = new double[3];
                if (inBands >= 3)
                {
                    rgb[0] = inPix[0]; rgb[1] = inPix[1]; rgb[2] = inPix[2];
                }
                else
                {
                    rgb[0] = rgb[1] = rgb[2] = inPix[0];
                }

                if (space == VipsInterpretation.BW)
                {
                    // RGB to Gray (Rec.709)
                    double gray = 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2];
                    outPix[0] = (byte)Math.Clamp(gray, 0, 255);
                }
                else if (space == VipsInterpretation.CMYK)
                {
                    // Naive RGB to CMYK
                    double r_norm = rgb[0] / 255.0;
                    double g_norm = rgb[1] / 255.0;
                    double b_norm = rgb[2] / 255.0;
                    double k = 1.0 - Math.Max(r_norm, Math.Max(g_norm, b_norm));
                    if (k < 1.0)
                    {
                        outPix[0] = (byte)((1.0 - r_norm - k) / (1.0 - k) * 255);
                        outPix[1] = (byte)((1.0 - g_norm - k) / (1.0 - k) * 255);
                        outPix[2] = (byte)((1.0 - b_norm - k) / (1.0 - k) * 255);
                    }
                    else
                    {
                        outPix[0] = outPix[1] = outPix[2] = 0;
                    }
                    outPix[3] = (byte)(k * 255);
                }
                else if (space == VipsInterpretation.scRGB || space == VipsInterpretation.XYZ || space == VipsInterpretation.Lab)
                {
                    // Floating point spaces
                    float[] floats = ToFloats(rgb, @in.Interpretation, space);
                    for (int i = 0; i < 3; i++)
                    {
                        BitConverter.TryWriteBytes(outPix.Slice(i * 4, 4), floats[i]);
                    }
                }
                else if ((space == VipsInterpretation.RGB || space == VipsInterpretation.SRGB) && @in.Bands == 1)
                {
                    // Gray to RGB
                    outPix[0] = outPix[1] = outPix[2] = (byte)rgb[0];
                }
                else
                {
                    // Fallback copy
                    for (int i = 0; i < Math.Min(inBands, outBands); i++)
                    {
                        outPix[i] = inPix[i];
                    }
                }
            }
        }

        return 0;
    }

    private static float[] ToFloats(double[] rgb, VipsInterpretation from, VipsInterpretation to)
    {
        // Simple sRGB to linear conversion for now
        float[] res = new float[3];
        for (int i = 0; i < 3; i++)
        {
            double val = rgb[i] / 255.0;
            res[i] = (float)(val <= 0.04045 ? val / 12.92 : Math.Pow((val + 0.055) / 1.055, 2.4));
        }

        if (to == VipsInterpretation.XYZ || to == VipsInterpretation.Lab)
        {
            // linear RGB to XYZ (D65)
            float x = (float)(0.4124564 * res[0] + 0.3575761 * res[1] + 0.1804375 * res[2]);
            float y = (float)(0.2126729 * res[0] + 0.7151522 * res[1] + 0.0721750 * res[2]);
            float z = (float)(0.0193339 * res[0] + 0.1191920 * res[1] + 0.9503041 * res[2]);
            
            if (to == VipsInterpretation.XYZ)
            {
                res[0] = x; res[1] = y; res[2] = z;
            }
            else // Lab
            {
                res[0] = (float)(116.0 * LabF(y) - 16.0);
                res[1] = (float)(500.0 * (LabF(x / 0.95047) - LabF(y)));
                res[2] = (float)(200.0 * (LabF(y) - LabF(z / 1.08883)));
            }
        }

        return res;
    }

    private static double LabF(double t) => t > 0.008856 ? Math.Pow(t, 1.0 / 3.0) : 7.787 * t + 16.0 / 116.0;
}
