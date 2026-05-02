using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Geometric;

/// <summary>
/// Tile the input <paramref name="Across"/> times horizontally and
/// <paramref name="Down"/> times vertically into an output of size
/// <c>(W * Across, H * Down)</c>.
///
/// <para>Mirrors libvips <c>vips_replicate</c>. Common use is
/// generating tile-test fixtures, building seamless texture mockups,
/// or filling a destination canvas with a pattern.</para>
///
/// <para>Each output region might span tile boundaries. We split the
/// requested rectangle into horizontal slabs aligned to the input
/// width and copy each slab's source row to one or more output
/// scanlines, so the inner loop runs at <c>memcpy</c> speed.</para>
/// </summary>
public class VipsReplicate : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int Across { get; set; } = 1;
    public int Down { get; set; } = 1;

    public override int Build()
    {
        if (In == null) return -1;
        if (Across < 1 || Down < 1) return -1;

        Out = new VipsImage
        {
            Width = In.Width * Across, Height = In.Height * Down,
            Bands = In.Bands, BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Replicate", RuntimeHelpers.GetHashCode(In), Across, Down);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        VipsRect r = outRegion.Valid;
        int W = @in.Width, H = @in.Height;
        int pelSize = @in.SizeOfPel;

        // Split the request into per-input-tile rectangles. For each
        // (tileY, tileX) the input request is the intersection of the
        // output rect with the tile range, mapped back into input
        // coordinates.
        int yEnd = r.Top + r.Height;
        int xEnd = r.Left + r.Width;

        for (int gy = r.Top; gy < yEnd; )
        {
            int srcY = gy % H;
            int rowsLeftInTile = H - srcY;
            int rowsToWrite = Math.Min(rowsLeftInTile, yEnd - gy);

            for (int gx = r.Left; gx < xEnd; )
            {
                int srcX = gx % W;
                int colsLeftInTile = W - srcX;
                int colsToWrite = Math.Min(colsLeftInTile, xEnd - gx);

                var inRect = new VipsRect(srcX, srcY, colsToWrite, rowsToWrite);
                if (inRegion.Prepare(inRect) != 0) return -1;
                int rowBytes = colsToWrite * pelSize;

                for (int dy = 0; dy < rowsToWrite; dy++)
                {
                    var inAddr = inRegion.GetAddress(srcX, srcY + dy);
                    var outAddr = outRegion.GetAddress(gx, gy + dy);
                    inAddr.Slice(0, rowBytes).CopyTo(outAddr);
                }
                gx += colsToWrite;
            }
            gy += rowsToWrite;
        }
        return 0;
    }
}
