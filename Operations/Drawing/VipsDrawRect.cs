using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Drawing;

public class VipsDrawRect : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public VipsRect Rect { get; set; }
    public byte[]? Ink { get; set; }
    public bool Fill { get; set; }

    public override int Build()
    {
        if (In == null || Ink == null) return -1;

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
            ClientB = new { Rect, Ink, Fill }
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        var hash = new HashCode();
        hash.Add("DrawRect");
        hash.Add(RuntimeHelpers.GetHashCode(In));
        hash.Add(Rect.Left); hash.Add(Rect.Top); hash.Add(Rect.Width); hash.Add(Rect.Height);
        if (Ink != null) foreach (var b in Ink) hash.Add(b);
        hash.Add(Fill);
        return hash.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        dynamic config = b!;
        VipsRect rect = config.Rect;
        byte[] ink = config.Ink;
        bool fill = config.Fill;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        int pelSize = @in.SizeOfPel;
        int rowSize = r.Width * pelSize;
        for (int y = 0; y < r.Height; y++)
        {
            inRegion.GetAddress(r.Left, r.Top + y).Slice(0, rowSize).CopyTo(outRegion.GetAddress(r.Left, r.Top + y));
        }

        if (fill)
        {
            VipsRect overlap = VipsRect.Intersect(r, rect);
            if (!overlap.IsEmpty)
            {
                for (int y = 0; y < overlap.Height; y++)
                {
                    var addr = outRegion.GetAddress(overlap.Left, overlap.Top + y);
                    for (int x = 0; x < overlap.Width; x++)
                    {
                        ink.AsSpan().CopyTo(addr.Slice(x * pelSize, pelSize));
                    }
                }
            }
        }
        else
        {
            // Outline
            DrawHLine(outRegion, rect.Left, rect.Top, rect.Width, ink);
            DrawHLine(outRegion, rect.Left, rect.Bottom - 1, rect.Width, ink);
            DrawVLine(outRegion, rect.Left, rect.Top, rect.Height, ink);
            DrawVLine(outRegion, rect.Right - 1, rect.Top, rect.Height, ink);
        }

        return 0;
    }

    private static void DrawHLine(VipsRegion reg, int x, int y, int width, byte[] ink)
    {
        if (y < reg.Valid.Top || y >= reg.Valid.Bottom) return;
        int left = Math.Max(x, reg.Valid.Left);
        int right = Math.Min(x + width, reg.Valid.Right);
        int pelSize = reg.Image.SizeOfPel;
        for (int i = left; i < right; i++)
        {
            ink.AsSpan().CopyTo(reg.GetAddress(i, y).Slice(0, pelSize));
        }
    }

    private static void DrawVLine(VipsRegion reg, int x, int y, int height, byte[] ink)
    {
        if (x < reg.Valid.Left || x >= reg.Valid.Right) return;
        int top = Math.Max(y, reg.Valid.Top);
        int bottom = Math.Min(y + height, reg.Valid.Bottom);
        int pelSize = reg.Image.SizeOfPel;
        for (int i = top; i < bottom; i++)
        {
            ink.AsSpan().CopyTo(reg.GetAddress(x, i).Slice(0, pelSize));
        }
    }
}
