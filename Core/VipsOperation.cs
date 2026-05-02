using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Core;

public abstract class VipsOperation
{
    public abstract int Build();
    public abstract int GetCacheKey();
}

/// <summary>
/// Canonical sequence helpers, mirroring libvips vips_start_one / vips_stop_one
/// (single input) and vips_start_many / vips_stop_many (array of inputs).
/// Operations attach these to <c>VipsImage.StartFn</c> / <c>StopFn</c> so each
/// VipsRegion gets pre-allocated input regions reused across tiles instead of
/// being constructed in every Generate call.
/// </summary>
public static class VipsSeq
{
    /// ClientA must be the input VipsImage. Returns a VipsRegion on it.
    public static object? StartOne(VipsImage @out, object? a, object? b)
    {
        return new VipsRegion((VipsImage)a!);
    }

    public static int StopOne(object? seq, object? a, object? b)
    {
        (seq as IDisposable)?.Dispose();
        return 0;
    }

    /// ClientA must be a VipsImage[]. Returns VipsRegion[] aligned to it.
    public static object? StartMany(VipsImage @out, object? a, object? b)
    {
        var ins = (VipsImage[])a!;
        var regions = new VipsRegion[ins.Length];
        for (int i = 0; i < ins.Length; i++) regions[i] = new VipsRegion(ins[i]);
        return regions;
    }

    public static int StopMany(object? seq, object? a, object? b)
    {
        if (seq is VipsRegion[] regions)
            foreach (var r in regions) r?.Dispose();
        return 0;
    }
}

public class VipsInvert : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }

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
    {
        return HashCode.Combine("Invert", RuntimeHelpers.GetHashCode(In));
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        // Float path: libvips convention for signed/float types is plain
        // negation (`out = -x`), not 255-x. Users who want UChar-style invert
        // on a Float image cast back to UChar first.
        if (@in.BandFormat == VipsBandFormat.Float)
        {
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
                        float v = BinaryPrimitives.ReadSingleLittleEndian(inAddr.Slice(off, 4));
                        BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice(off, 4), -v);
                    }
                }
            }
            return 0;
        }

        int totalBytes = r.Width * @in.Bands;
        int vectorSize = System.Numerics.Vector<byte>.Count;
        var vec255 = new System.Numerics.Vector<byte>(255);

        for (int y = 0; y < r.Height; y++)
        {
            var inAddr = inRegion.GetAddress(r.Left, r.Top + y);
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);

            int i = 0;
            for (; i <= totalBytes - vectorSize; i += vectorSize)
            {
                var v = new System.Numerics.Vector<byte>(inAddr.Slice(i));
                var res = vec255 - v;
                res.CopyTo(outAddr.Slice(i));
            }

            // Remainder
            for (; i < totalBytes; i++)
            {
                outAddr[i] = (byte)(255 - inAddr[i]);
            }
        }

        return 0;
    }
}
