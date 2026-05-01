using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Drawing;

public class VipsDrawLine : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }
    public byte[]? Ink { get; set; }

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
            ClientB = new { X1, Y1, X2, Y2, Ink }
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);

        return 0;
    }

    public override int GetCacheKey()
    {
        var hash = new HashCode();
        hash.Add("DrawLine");
        hash.Add(RuntimeHelpers.GetHashCode(In));
        hash.Add(X1); hash.Add(Y1); hash.Add(X2); hash.Add(Y2);
        if (Ink != null) foreach (var b in Ink) hash.Add(b);
        return hash.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        dynamic config = b!;
        int x1 = config.X1; int y1 = config.Y1;
        int x2 = config.X2; int y2 = config.Y2;
        byte[] ink = config.Ink;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        int pelSize = @in.SizeOfPel;
        for (int y = 0; y < r.Height; y++)
        {
            inRegion.GetAddress(r.Left, r.Top + y).Slice(0, r.Width * pelSize).CopyTo(outRegion.GetAddress(r.Left, r.Top + y));
        }

        // Xiaolin Wu's antialiased line algorithm. Each pixel along the line
        // is blended with the existing background by a coverage factor in
        // [0, 1] derived from the pixel's distance from the ideal line. Two
        // pixels are written per step (above and below the line); endpoints
        // get extra weighting from the sub-pixel position of (x1,y1)/(x2,y2).
        int copyLen = Math.Min(ink.Length, pelSize);

        void Plot(int x, int y, double coverage)
        {
            if (!outRegion.Valid.Includes(x, y)) return;
            var addr = outRegion.GetAddress(x, y);
            double inv = 1.0 - coverage;
            for (int b = 0; b < copyLen; b++)
                addr[b] = (byte)(addr[b] * inv + ink[b] * coverage);
        }
        static double Frac(double v) => v - Math.Floor(v);

        // Axis-aligned fast paths. Wu's algorithm gives partial coverage at
        // endpoints — correct for diagonals, but for horizontal/vertical lines
        // users expect full-ink output. Detect and short-circuit those cases.
        if (y1 == y2)
        {
            int xa = Math.Min(x1, x2);
            int xb = Math.Max(x1, x2);
            for (int xi = xa; xi <= xb; xi++) Plot(xi, y1, 1.0);
            return 0;
        }
        if (x1 == x2)
        {
            int ya = Math.Min(y1, y2);
            int yb = Math.Max(y1, y2);
            for (int yi = ya; yi <= yb; yi++) Plot(x1, yi, 1.0);
            return 0;
        }

        double X1 = x1, Y1 = y1, X2 = x2, Y2 = y2;
        bool steep = Math.Abs(Y2 - Y1) > Math.Abs(X2 - X1);
        if (steep) { (X1, Y1) = (Y1, X1); (X2, Y2) = (Y2, X2); }
        if (X1 > X2) { (X1, X2) = (X2, X1); (Y1, Y2) = (Y2, Y1); }

        double DX = X2 - X1;
        double DY = Y2 - Y1;
        double gradient = DX == 0 ? 1.0 : DY / DX;

        // First endpoint
        double xend = Math.Round(X1);
        double yend = Y1 + gradient * (xend - X1);
        double xgap = 1.0 - Frac(X1 + 0.5);
        int xpxl1 = (int)xend;
        int ypxl1 = (int)Math.Floor(yend);
        if (steep)
        {
            Plot(ypxl1,     xpxl1, (1.0 - Frac(yend)) * xgap);
            Plot(ypxl1 + 1, xpxl1, Frac(yend) * xgap);
        }
        else
        {
            Plot(xpxl1, ypxl1,     (1.0 - Frac(yend)) * xgap);
            Plot(xpxl1, ypxl1 + 1, Frac(yend) * xgap);
        }
        double intery = yend + gradient;

        // Second endpoint
        xend = Math.Round(X2);
        yend = Y2 + gradient * (xend - X2);
        xgap = Frac(X2 + 0.5);
        int xpxl2 = (int)xend;
        int ypxl2 = (int)Math.Floor(yend);
        if (steep)
        {
            Plot(ypxl2,     xpxl2, (1.0 - Frac(yend)) * xgap);
            Plot(ypxl2 + 1, xpxl2, Frac(yend) * xgap);
        }
        else
        {
            Plot(xpxl2, ypxl2,     (1.0 - Frac(yend)) * xgap);
            Plot(xpxl2, ypxl2 + 1, Frac(yend) * xgap);
        }

        // Main loop
        if (steep)
        {
            for (int xi = xpxl1 + 1; xi < xpxl2; xi++)
            {
                int yi = (int)Math.Floor(intery);
                Plot(yi,     xi, 1.0 - Frac(intery));
                Plot(yi + 1, xi, Frac(intery));
                intery += gradient;
            }
        }
        else
        {
            for (int xi = xpxl1 + 1; xi < xpxl2; xi++)
            {
                int yi = (int)Math.Floor(intery);
                Plot(xi, yi,     1.0 - Frac(intery));
                Plot(xi, yi + 1, Frac(intery));
                intery += gradient;
            }
        }

        return 0;
    }
}
