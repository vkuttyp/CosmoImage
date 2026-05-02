using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Geometric;

/// <summary>
/// Toroidal shift — wrap-around translate. Output pixel <c>(x, y)</c>
/// reads from input <c>((x + dx) mod W, (y + dy) mod H)</c>.
///
/// <para>Mirrors libvips <c>vips_wrap</c>. The default offset puts the
/// image origin at the centre — useful as the spatial-domain
/// counterpart to a Fourier shift, and for tiling/seam tests.</para>
///
/// <para>The implementation slabs each output region into up to four
/// rectangles aligned to the input axes so memcpy stays the inner
/// loop, exactly as <see cref="VipsReplicate"/> does.</para>
/// </summary>
public class VipsWrap : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    /// <summary>Horizontal shift. Default 0 means "centre" (W/2).</summary>
    public int X { get; set; } = int.MinValue;
    /// <summary>Vertical shift. Default 0 means "centre" (H/2).</summary>
    public int Y { get; set; } = int.MinValue;

    public override int Build()
    {
        if (In == null) return -1;
        int dx = X == int.MinValue ? In.Width / 2 : X;
        int dy = Y == int.MinValue ? In.Height / 2 : Y;
        // Reduce into [0, W) / [0, H).
        dx = ((dx % In.Width) + In.Width) % In.Width;
        dy = ((dy % In.Height) + In.Height) % In.Height;

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height,
            Bands = In.Bands, BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = (dx, dy),
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Wrap", RuntimeHelpers.GetHashCode(In), X, Y);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        var (dx, dy) = ((int, int))b!;
        VipsRect r = outRegion.Valid;
        int W = @in.Width, H = @in.Height;
        int pelSize = @in.SizeOfPel;

        int yEnd = r.Top + r.Height;
        int xEnd = r.Left + r.Width;

        for (int gy = r.Top; gy < yEnd; )
        {
            int srcY = (gy + dy) % H;
            int rowsLeft = H - srcY;
            int rowsToWrite = Math.Min(rowsLeft, yEnd - gy);

            for (int gx = r.Left; gx < xEnd; )
            {
                int srcX = (gx + dx) % W;
                int colsLeft = W - srcX;
                int colsToWrite = Math.Min(colsLeft, xEnd - gx);

                var inRect = new VipsRect(srcX, srcY, colsToWrite, rowsToWrite);
                if (inRegion.Prepare(inRect) != 0) return -1;
                int rowBytes = colsToWrite * pelSize;

                for (int sy = 0; sy < rowsToWrite; sy++)
                {
                    var inAddr = inRegion.GetAddress(srcX, srcY + sy);
                    var outAddr = outRegion.GetAddress(gx, gy + sy);
                    inAddr.Slice(0, rowBytes).CopyTo(outAddr);
                }
                gx += colsToWrite;
            }
            gy += rowsToWrite;
        }
        return 0;
    }
}
