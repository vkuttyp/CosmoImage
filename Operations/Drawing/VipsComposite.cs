using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Drawing;

public class VipsComposite : VipsOperation
{
    public VipsImage? Base { get; set; }
    public VipsImage? Overlay { get; set; }
    public VipsImage? Out { get; set; }
    public int X { get; set; }
    public int Y { get; set; }

    public override int Build()
    {
        if (Base == null || Overlay == null) return -1;

        Out = new VipsImage
        {
            Width = Base.Width,
            Height = Base.Height,
            Bands = Base.Bands,
            BandFormat = Base.BandFormat,
            Interpretation = Base.Interpretation,
            Coding = Base.Coding,
            XRes = Base.XRes,
            YRes = Base.YRes,
            StartFn = VipsSeq.StartMany,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopMany,
            ClientA = new[] { Base, Overlay },
            ClientB = new { X, Y }
        };
        Out.CopyMetadataFrom(Base);
        Out.SetPipeline(VipsDemandStyle.Any, Base, Overlay);

        return 0;
    }

    public override int GetCacheKey()
    {
        return HashCode.Combine("Composite", RuntimeHelpers.GetHashCode(Base), RuntimeHelpers.GetHashCode(Overlay), X, Y);
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var baseRegion = regions[0];
        var overlayRegion = regions[1];
        VipsImage @base = baseRegion.Image;
        VipsImage overlay = overlayRegion.Image;
        dynamic offset = b!;
        int ox = offset.X;
        int oy = offset.Y;
        VipsRect r = outRegion.Valid;

        if (baseRegion.Prepare(r) != 0) return -1;

        // Determine overlap with overlay
        VipsRect overlayRect = new VipsRect(ox, oy, overlay.Width, overlay.Height);
        VipsRect overlap = VipsRect.Intersect(r, overlayRect);

        // Copy base pixels to output first
        int pelSize = @base.SizeOfPel;
        for (int y = 0; y < r.Height; y++)
        {
            baseRegion.GetAddress(r.Left, r.Top + y).Slice(0, r.Width * pelSize).CopyTo(outRegion.GetAddress(r.Left, r.Top + y));
        }

        if (!overlap.IsEmpty)
        {
            // Request local coordinates from overlay
            VipsRect overlayRequest = new VipsRect(overlap.Left - ox, overlap.Top - oy, overlap.Width, overlap.Height);
            if (overlayRegion.Prepare(overlayRequest) != 0) return -1;

            bool hasAlpha = overlay.Bands == 2 || overlay.Bands == 4;
            int overlayBands = overlay.Bands;
            int alphaIdx = overlayBands - 1;

            for (int y = 0; y < overlap.Height; y++)
            {
                var srcAddr = overlayRegion.GetAddress(overlayRequest.Left, overlayRequest.Top + y);
                var destAddr = outRegion.GetAddress(overlap.Left, overlap.Top + y);

                for (int x = 0; x < overlap.Width; x++)
                {
                    float alpha = hasAlpha ? srcAddr[x * overlayBands + alphaIdx] / 255.0f : 1.0f;
                    if (alpha == 0) continue;

                    for (int i = 0; i < Math.Min(@base.Bands, overlay.Bands - (hasAlpha ? 1 : 0)); i++)
                    {
                        int baseIdx = x * pelSize + i;
                        int overIdx = x * overlayBands + i;
                        destAddr[baseIdx] = (byte)(destAddr[baseIdx] * (1 - alpha) + srcAddr[overIdx] * alpha);
                    }
                }
            }
        }

        return 0;
    }
}
