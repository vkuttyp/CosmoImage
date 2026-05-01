using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Geometric;

public class VipsRotate : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public VipsAngle Angle { get; set; }

    public override int Build()
    {
        if (In == null) return -1;

        int width = In.Width;
        int height = In.Height;

        if (Angle == VipsAngle.D90 || Angle == VipsAngle.D270)
        {
            width = In.Height;
            height = In.Width;
        }

        Out = new VipsImage
        {
            Width = width,
            Height = height,
            Bands = In.Bands,
            BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding,
            XRes = In.YRes, // Swap resolution if 90/270
            YRes = In.XRes,
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In,
            ClientB = Angle
        };

        if (Angle == VipsAngle.D0 || Angle == VipsAngle.D180)
        {
            Out.XRes = In.XRes;
            Out.YRes = In.YRes;
        }

        // D0/D180 are pointwise; D90/D270 transpose axes (random-ish access).
        var hint = (Angle == VipsAngle.D0 || Angle == VipsAngle.D180)
            ? VipsDemandStyle.Any
            : VipsDemandStyle.SmallTile;
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(hint, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        return HashCode.Combine("Rotate", RuntimeHelpers.GetHashCode(In), Angle);
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        VipsAngle angle = (VipsAngle)b!;
        VipsRect r = outRegion.Valid;

        int pelSize = @in.SizeOfPel;

        switch (angle)
        {
            case VipsAngle.D0:
                {
                    if (inRegion.Prepare(r) != 0) return -1;
                    for (int y = 0; y < r.Height; y++)
                    {
                        inRegion.GetAddress(r.Left, r.Top + y).Slice(0, r.Width * pelSize).CopyTo(outRegion.GetAddress(r.Left, r.Top + y));
                    }
                }
                break;

            case VipsAngle.D180:
                {
                    // 180 is both horizontal and vertical flip
                    VipsRect inRect = new VipsRect(@in.Width - 1 - (r.Left + r.Width - 1), @in.Height - 1 - (r.Top + r.Height - 1), r.Width, r.Height);
                    if (inRegion.Prepare(inRect) != 0) return -1;
                    for (int y = 0; y < r.Height; y++)
                    {
                        var inLine = inRegion.GetAddress(inRect.Left, inRect.Top + (r.Height - 1 - y));
                        var outLine = outRegion.GetAddress(r.Left, r.Top + y);
                        for (int x = 0; x < r.Width; x++)
                        {
                            inLine.Slice((r.Width - 1 - x) * pelSize, pelSize).CopyTo(outLine.Slice(x * pelSize, pelSize));
                        }
                    }
                }
                break;

            case VipsAngle.D90:
                {
                    // 90 deg clockwise: out(x, y) = in(r.Top + y, in.Height - 1 - (r.Left + x))
                    // Single Prepare over the entire input rect needed for this output region.
                    VipsRect inRect = new VipsRect(
                        r.Top, @in.Height - r.Right,
                        r.Height, r.Width);
                    if (inRegion.Prepare(inRect) != 0) return -1;
                    for (int y = 0; y < r.Height; y++)
                    {
                        var outLine = outRegion.GetAddress(r.Left, r.Top + y);
                        int inX = r.Top + y;
                        for (int x = 0; x < r.Width; x++)
                        {
                            int inY = @in.Height - 1 - (r.Left + x);
                            inRegion.GetAddress(inX, inY).Slice(0, pelSize).CopyTo(outLine.Slice(x * pelSize, pelSize));
                        }
                    }
                }
                break;

            case VipsAngle.D270:
                {
                    // 270 deg clockwise: out(x, y) = in(in.Width - 1 - (r.Top + y), r.Left + x)
                    VipsRect inRect = new VipsRect(
                        @in.Width - r.Bottom, r.Left,
                        r.Height, r.Width);
                    if (inRegion.Prepare(inRect) != 0) return -1;
                    for (int y = 0; y < r.Height; y++)
                    {
                        var outLine = outRegion.GetAddress(r.Left, r.Top + y);
                        int inX = @in.Width - 1 - (r.Top + y);
                        for (int x = 0; x < r.Width; x++)
                        {
                            int inY = r.Left + x;
                            inRegion.GetAddress(inX, inY).Slice(0, pelSize).CopyTo(outLine.Slice(x * pelSize, pelSize));
                        }
                    }
                }
                break;
        }

        return 0;
    }
}
