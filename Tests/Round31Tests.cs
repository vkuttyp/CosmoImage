using System;
using System.Buffers.Binary;
using Xunit;

namespace CosmoImage.Tests;

public class Round31Tests
{
    private static VipsImage Solid(int w, int h, byte v)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = v;
                }
                return 0;
            }
        };

    /// <summary>Sharp horizontal step: left half black, right half white.</summary>
    private static VipsImage VStep(int w, int h, int splitCol)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = (byte)((reg.Valid.Left + x) >= splitCol ? 255 : 0);
                }
                return 0;
            }
        };

    /// <summary>Single non-zero pixel at (px, py); rest zero.</summary>
    private static VipsImage SingleDot(int w, int h, int px, int py)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    int gy = reg.Valid.Top + y;
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        addr[x] = (byte)((gx == px && gy == py) ? 255 : 0);
                    }
                }
                return 0;
            }
        };

    /// <summary>Two disjoint non-zero blobs (one 2×2 top-left, one 2×2 bottom-right).</summary>
    private static VipsImage TwoBlobs(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    int gy = reg.Valid.Top + y;
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        bool blobA = gx < 2 && gy < 2;
                        bool blobB = gx >= w - 2 && gy >= h - 2;
                        addr[x] = (byte)((blobA || blobB) ? 200 : 0);
                    }
                }
                return 0;
            }
        };

    // ---- Sobel ----

    [Fact]
    public void Sobel_FlatImage_HasZeroResponse()
    {
        var src = Solid(8, 8, 100);
        var edges = src.Sobel();
        using var reg = new VipsRegion(edges);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        Assert.Equal(0, reg.GetAddress(4, 4)[0]);
    }

    [Fact]
    public void Sobel_StepEdge_SpikesAtTransition()
    {
        var src = VStep(10, 10, splitCol: 5);
        var edges = src.Sobel();
        using var reg = new VipsRegion(edges);
        reg.Prepare(new VipsRect(0, 0, 10, 10));
        // The transition column should max out (clamped at 255).
        Assert.Equal(255, reg.GetAddress(4, 5)[0]);
        // Far from the edge, response is zero.
        Assert.Equal(0, reg.GetAddress(0, 5)[0]);
        Assert.Equal(0, reg.GetAddress(9, 5)[0]);
    }

    // ---- Compass ----

    [Fact]
    public void Compass_StepEdge_RespondsAtTransition()
    {
        var src = VStep(10, 10, splitCol: 5);
        var edges = src.Compass();
        using var reg = new VipsRegion(edges);
        reg.Prepare(new VipsRect(0, 0, 10, 10));
        // Transition column has strong response.
        Assert.True(reg.GetAddress(4, 5)[0] > 100);
        Assert.True(reg.GetAddress(5, 5)[0] > 100);
        // Far from the edge, response is zero.
        Assert.Equal(0, reg.GetAddress(0, 5)[0]);
    }

    // ---- Canny ----

    [Fact]
    public void Canny_StepEdge_ProducesBinaryEdge()
    {
        var src = VStep(20, 20, splitCol: 10);
        var edges = src.Canny(sigma: 1.0, low: 20, high: 60);
        using var reg = new VipsRegion(edges);
        reg.Prepare(new VipsRect(0, 0, 20, 20));
        // Some pixel near the seam should be a 255-edge.
        bool foundEdge = false;
        for (int y = 5; y < 15 && !foundEdge; y++)
            for (int x = 7; x < 13 && !foundEdge; x++)
                if (reg.GetAddress(x, y)[0] == 255) foundEdge = true;
        Assert.True(foundEdge, "Canny should detect the step edge near the seam");
        // Far from the edge, the output is zero.
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void Canny_FlatImage_NoEdges()
    {
        var src = Solid(16, 16, 100);
        var edges = src.Canny();
        using var reg = new VipsRegion(edges);
        reg.Prepare(new VipsRect(0, 0, 16, 16));
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                Assert.Equal(0, reg.GetAddress(x, y)[0]);
    }

    // ---- Sharpen ----

    [Fact]
    public void Sharpen_StepEdge_AmplifiesContrast()
    {
        var src = VStep(20, 20, splitCol: 10);
        var sharp = src.Sharpen(sigma: 1.0, m1: 2.0, m2: 2.0, x1: 0);
        using var reg = new VipsRegion(sharp);
        reg.Prepare(new VipsRect(0, 0, 20, 20));
        // Just-left-of-edge darkens (≤ original 0), just-right brightens (≥ original 255).
        Assert.Equal(0, reg.GetAddress(9, 10)[0]);
        Assert.True(reg.GetAddress(10, 10)[0] >= 254);
        // Far from the edge, sharpen leaves values approximately unchanged.
        Assert.True(reg.GetAddress(0, 10)[0] <= 1);
        Assert.True(reg.GetAddress(19, 10)[0] >= 254);
    }

    [Fact]
    public void Sharpen_DeadBand_SuppressesNoise()
    {
        // Tiny gradient, well below the X1 threshold → output = input.
        var src = new VipsImage
        {
            Width = 8, Height = 8, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = (byte)(100 + (x & 1));
                }
                return 0;
            }
        };
        var sharp = src.Sharpen(sigma: 0.5, x1: 50);
        using var ra = new VipsRegion(src);
        using var rb = new VipsRegion(sharp);
        ra.Prepare(new VipsRect(0, 0, 8, 8));
        rb.Prepare(new VipsRect(0, 0, 8, 8));
        for (int x = 0; x < 8; x++)
            Assert.Equal(ra.GetAddress(x, 4)[0], rb.GetAddress(x, 4)[0]);
    }

    // ---- Nearest (distance transform) ----

    [Fact]
    public void Nearest_OnSinglePixel_RadiatesDistance()
    {
        var src = SingleDot(20, 20, px: 10, py: 10);
        var dt = src.Nearest();
        using var reg = new VipsRegion(dt);
        reg.Prepare(new VipsRect(0, 0, 20, 20));
        // The seed pixel itself is at distance 0.
        Assert.Equal(0, reg.GetAddress(10, 10)[0]);
        // Adjacent (1-step Manhattan) is at Euclidean distance 1.
        Assert.Equal(1, reg.GetAddress(11, 10)[0]);
        Assert.Equal(1, reg.GetAddress(10, 11)[0]);
        // Diagonal is sqrt(2) ≈ 1 (after sqrt-then-clamp-to-byte).
        Assert.Equal(1, reg.GetAddress(11, 11)[0]);
        // 5 away horizontally is exactly 5.
        Assert.Equal(5, reg.GetAddress(15, 10)[0]);
        // 3,4 → distance 5 exactly.
        Assert.Equal(5, reg.GetAddress(13, 14)[0]);
    }

    [Fact]
    public void Nearest_MultipleSeeds_PicksClosest()
    {
        // Two seeds at (0, 0) and (9, 0); pixel (5, 0) is closer to (9, 0) (dist 4).
        var src = new VipsImage
        {
            Width = 10, Height = 1, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                var addr = reg.GetAddress(reg.Valid.Left, 0);
                for (int x = 0; x < reg.Valid.Width; x++)
                {
                    int gx = reg.Valid.Left + x;
                    addr[x] = (byte)((gx == 0 || gx == 9) ? 255 : 0);
                }
                return 0;
            }
        };
        var dt = src.Nearest();
        using var reg = new VipsRegion(dt);
        reg.Prepare(new VipsRect(0, 0, 10, 1));
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.Equal(0, reg.GetAddress(9, 0)[0]);
        Assert.Equal(4, reg.GetAddress(4, 0)[0]); // closer to 0
        Assert.Equal(4, reg.GetAddress(5, 0)[0]); // closer to 9
    }

    // ---- LabelRegions ----

    [Fact]
    public void LabelRegions_TwoBlobs_GetTwoLabels()
    {
        var src = TwoBlobs(8, 8);
        var labels = src.LabelRegions();
        Assert.Equal(VipsBandFormat.UInt, labels.BandFormat);

        using var reg = new VipsRegion(labels);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        uint topLeft = BinaryPrimitives.ReadUInt32LittleEndian(reg.GetAddress(0, 0).Slice(0, 4));
        uint bottomRight = BinaryPrimitives.ReadUInt32LittleEndian(reg.GetAddress(7, 7).Slice(0, 4));
        uint background = BinaryPrimitives.ReadUInt32LittleEndian(reg.GetAddress(4, 4).Slice(0, 4));

        Assert.NotEqual(0u, topLeft);
        Assert.NotEqual(0u, bottomRight);
        Assert.NotEqual(topLeft, bottomRight);
        Assert.Equal(0u, background);
    }

    [Fact]
    public void LabelRegions_ConnectedBlob_IsOneLabel()
    {
        // L-shape: 4 pixels all 4-connected.
        var src = new VipsImage
        {
            Width = 4, Height = 4, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    int gy = reg.Valid.Top + y;
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        int gx = reg.Valid.Left + x;
                        bool fg = (gx == 0 && gy < 3) || (gy == 2 && gx < 3);
                        addr[x] = (byte)(fg ? 200 : 0);
                    }
                }
                return 0;
            }
        };
        var labels = src.LabelRegions();
        using var reg = new VipsRegion(labels);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        uint a = BinaryPrimitives.ReadUInt32LittleEndian(reg.GetAddress(0, 0).Slice(0, 4));
        uint b = BinaryPrimitives.ReadUInt32LittleEndian(reg.GetAddress(2, 2).Slice(0, 4));
        Assert.Equal(a, b);
        Assert.NotEqual(0u, a);
    }
}
