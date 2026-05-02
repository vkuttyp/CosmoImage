using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Convolution;

public enum VipsMorphMethod
{
    Dilate = 0,
    Erode = 1
}

public class VipsMorph : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public double[,]? Mask { get; set; }
    public VipsMorphMethod Method { get; set; }

    public override int Build()
    {
        if (In == null || Mask == null) return -1;

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
            ClientB = new { Mask, Method }
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.FatStrip, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        var hash = new HashCode();
        hash.Add("Morph");
        hash.Add(RuntimeHelpers.GetHashCode(In));
        if (Mask != null) foreach (var m in Mask) hash.Add(m);
        hash.Add(Method);
        return hash.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        dynamic config = b!;
        double[,] mask = config.Mask;
        VipsMorphMethod method = config.Method;
        VipsRect r = outRegion.Valid;

        int mh = mask.GetLength(0);
        int mw = mask.GetLength(1);
        int ox = mw / 2;
        int oy = mh / 2;

        VipsRect inRect = new VipsRect(r.Left - ox, r.Top - oy, r.Width + mw - 1, r.Height + mh - 1);
        VipsRect clipped = VipsRect.Intersect(inRect, new VipsRect(0, 0, @in.Width, @in.Height));
        if (inRegion.Prepare(clipped) != 0) return -1;

        int bands = @in.Bands;

        if (@in.BandFormat == VipsBandFormat.Float)
            return GenerateFloat(inRegion, outRegion, @in, mask, method, r, mw, mh, ox, oy, bands);

        int pelSize = @in.SizeOfPel;

        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    byte extreme = method == VipsMorphMethod.Dilate ? (byte)0 : (byte)255;

                    for (int my = 0; mh > my; my++)
                    {
                        for (int mx = 0; mw > mx; mx++)
                        {
                            if (mask[my, mx] == 0) continue;

                            int ix = r.Left + x + mx - ox;
                            int iy = r.Top + y + my - oy;

                            if (ix >= 0 && ix < @in.Width && iy >= 0 && iy < @in.Height)
                            {
                                byte val = inRegion.GetAddress(ix, iy)[bnd];
                                if (method == VipsMorphMethod.Dilate)
                                    extreme = Math.Max(extreme, val);
                                else
                                    extreme = Math.Min(extreme, val);
                            }
                        }
                    }
                    outAddr[x * pelSize + bnd] = extreme;
                }
            }
        }

        return 0;
    }

    private static int GenerateFloat(VipsRegion inRegion, VipsRegion outRegion, VipsImage @in, double[,] mask, VipsMorphMethod method, VipsRect r, int mw, int mh, int ox, int oy, int bands)
    {
        // Float morphology uses the actual numerical extrema with no [0,255]
        // bracket. Seed Dilate with -∞, Erode with +∞ so the first valid
        // sample replaces the seed regardless of its sign.
        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
            {
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    float extreme = method == VipsMorphMethod.Dilate ? float.NegativeInfinity : float.PositiveInfinity;
                    bool any = false;
                    for (int my = 0; my < mh; my++)
                    {
                        for (int mx = 0; mx < mw; mx++)
                        {
                            if (mask[my, mx] == 0) continue;
                            int ix = r.Left + x + mx - ox;
                            int iy = r.Top + y + my - oy;
                            if (ix >= 0 && ix < @in.Width && iy >= 0 && iy < @in.Height)
                            {
                                var inPel = inRegion.GetAddress(ix, iy);
                                float val = BinaryPrimitives.ReadSingleLittleEndian(inPel.Slice(bnd * 4, 4));
                                extreme = method == VipsMorphMethod.Dilate
                                    ? MathF.Max(extreme, val)
                                    : MathF.Min(extreme, val);
                                any = true;
                            }
                        }
                    }
                    // No valid samples (full out-of-bounds + non-zero mask) → 0.
                    if (!any) extreme = 0;
                    BinaryPrimitives.WriteSingleLittleEndian(outAddr.Slice((x * bands + bnd) * 4, 4), extreme);
                }
            }
        }
        return 0;
    }
}
