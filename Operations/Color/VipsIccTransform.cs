using System;
using System.IO;
using System.Runtime.CompilerServices;
using ImageMagick;

namespace CosmoImage.Operations.Color;

public class VipsIccTransform : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public byte[]? InputProfile { get; set; }
    public byte[]? OutputProfile { get; set; }
    public VipsInterpretation Intent { get; set; } = VipsInterpretation.SRGB;

    public override int Build()
    {
        if (In == null || OutputProfile == null) return -1;

        Out = new VipsImage
        {
            Width = In.Width,
            Height = In.Height,
            Bands = 3, // Target profile is usually RGB
            BandFormat = VipsBandFormat.UChar,
            Interpretation = Intent,
            Coding = In.Coding,
            XRes = In.XRes,
            YRes = In.YRes,
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In,
            ClientB = new { InputProfile, OutputProfile }
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        var hash = new HashCode();
        hash.Add("IccTransform");
        hash.Add(RuntimeHelpers.GetHashCode(In));
        if (InputProfile != null) hash.Add(InputProfile.Length);
        if (OutputProfile != null) hash.Add(OutputProfile.Length);
        hash.Add(Intent);
        return hash.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        dynamic profiles = b!;
        byte[]? inputProfile = profiles.InputProfile;
        byte[] outputProfile = profiles.OutputProfile;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        // Convert current region to MagickImage for profile transform
        // This is inefficient (re-encoding/decoding) but works without LittleCMS
        // A better way would be to use MagickImage.SetPixels but it's complex for regions.
        
        int bands = @in.Bands;
        byte[] pix = new byte[r.Width * r.Height * bands];
        for (int y = 0; y < r.Height; y++)
        {
            var line = inRegion.GetAddress(r.Left, r.Top + y);
            line.Slice(0, r.Width * bands).CopyTo(pix.AsSpan(y * r.Width * bands));
        }

        using var magickImage = new MagickImage();
        var settings = new MagickReadSettings
        {
            Width = (uint)r.Width,
            Height = (uint)r.Height,
            Format = bands == 3 ? MagickFormat.Rgb : (bands == 1 ? MagickFormat.Gray : MagickFormat.Rgba)
        };
        magickImage.Read(pix, settings);

        if (inputProfile != null)
            magickImage.SetProfile(new ColorProfile(inputProfile));
        
        magickImage.SetProfile(new ColorProfile(outputProfile));

        using var outPixels = magickImage.GetPixels();
        var outData = outPixels.ToByteArray(0, 0, (uint)r.Width, (uint)r.Height, "RGB");

        if (outData == null) return -1;

        int outBands = 3;
        for (int y = 0; y < r.Height; y++)
        {
            var destLine = outRegion.GetAddress(r.Left, r.Top + y);
            outData.AsSpan(y * r.Width * outBands, r.Width * outBands).CopyTo(destLine);
        }

        return 0;
    }
}
