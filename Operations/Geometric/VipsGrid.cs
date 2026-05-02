using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Geometric;

/// <summary>
/// Lay a tall stack of equal-sized tiles into a 2D grid. Input must be
/// <c>(tileWidth, N · tileHeight)</c> for some <paramref name="N"/>;
/// output is <c>(tileWidth · cols, tileHeight · rows)</c> where
/// <c>rows · cols ≥ N</c>. Tile <c>k</c> (0-indexed top-to-bottom in the
/// input) lands at grid cell <c>(k mod cols, k div cols)</c>. Mirrors
/// libvips <c>vips_grid</c>.
///
/// <para>Common use: turning an animation strip (or DZI tile column)
/// into a contact-sheet-style grid; converting a video frame stack to
/// a 2D mosaic for inspection. If <c>N &lt; rows · cols</c>, trailing
/// cells are filled with zero.</para>
/// </summary>
public class VipsGrid : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int TileHeight { get; set; }
    public int Across { get; set; }
    public int Down { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (TileHeight < 1 || Across < 1 || Down < 1) return -1;
        if (In.Height % TileHeight != 0) return -1;

        Out = new VipsImage
        {
            Width = In.Width * Across,
            Height = TileHeight * Down,
            Bands = In.Bands, BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = (TileHeight, Across, Down),
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Grid", RuntimeHelpers.GetHashCode(In), TileHeight, Across, Down);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        var (tileH, across, _down) = ((int, int, int))b!;
        int tileW = @in.Width;
        int totalTilesAvailable = @in.Height / tileH;
        VipsRect r = outRegion.Valid;
        int pelSize = @in.SizeOfPel;

        // For each output scanline, figure which tile-row we're in,
        // then walk across the tile-columns the rect covers.
        int yEnd = r.Top + r.Height;
        int xEnd = r.Left + r.Width;

        for (int gy = r.Top; gy < yEnd; )
        {
            int rowIdx = gy / tileH;
            int tileLocalY = gy % tileH;
            int rowsLeftInTile = tileH - tileLocalY;
            int rowsToWrite = Math.Min(rowsLeftInTile, yEnd - gy);

            for (int gx = r.Left; gx < xEnd; )
            {
                int colIdx = gx / tileW;
                int tileLocalX = gx % tileW;
                int colsLeftInTile = tileW - tileLocalX;
                int colsToWrite = Math.Min(colsLeftInTile, xEnd - gx);
                int tileK = rowIdx * across + colIdx;

                if (tileK < totalTilesAvailable)
                {
                    int srcY = tileK * tileH + tileLocalY;
                    var inRect = new VipsRect(tileLocalX, srcY, colsToWrite, rowsToWrite);
                    if (inRegion.Prepare(inRect) != 0) return -1;
                    int rowBytes = colsToWrite * pelSize;

                    for (int sy = 0; sy < rowsToWrite; sy++)
                    {
                        var inAddr = inRegion.GetAddress(tileLocalX, srcY + sy);
                        var outAddr = outRegion.GetAddress(gx, gy + sy);
                        inAddr.Slice(0, rowBytes).CopyTo(outAddr);
                    }
                }
                else
                {
                    // Cell beyond available tiles → zero fill.
                    int rowBytes = colsToWrite * pelSize;
                    for (int sy = 0; sy < rowsToWrite; sy++)
                    {
                        var outAddr = outRegion.GetAddress(gx, gy + sy);
                        outAddr.Slice(0, rowBytes).Clear();
                    }
                }
                gx += colsToWrite;
            }
            gy += rowsToWrite;
        }
        return 0;
    }
}
