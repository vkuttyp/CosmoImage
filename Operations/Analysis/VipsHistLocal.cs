using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Contrast Limited Adaptive Histogram Equalization (CLAHE) — Pizer/Zuiderveld
/// 1994. Equalises the histogram of each tile in a grid independently,
/// then bilinearly interpolates between the four surrounding tile-CDFs at
/// each pixel to produce a smooth, locally-adapted transfer.
///
/// <para>Why "contrast limited": a plain per-tile equalisation amplifies
/// noise in mostly-flat regions because the CDF gets a near-vertical step
/// where most pixels share a few values. The algorithm clips each tile's
/// histogram at <c>clipLimit × (tile_pixels / 256)</c>, redistributes the
/// clipped excess uniformly across all bins, then equalises. Common
/// clipLimit values are 2.0–4.0; libvips defaults to 3.</para>
///
/// <para>UChar only for first cut (the 256-bin histogram is fundamentally
/// UChar-shaped). Per-band — no luminance conversion. For RGB input that's
/// suboptimal compared to "convert to Lab, equalise L, convert back", but
/// matches what libvips' <c>hist_local</c> does and what ImageSharp's
/// <c>AdaptiveHistogramEqualization</c> does. Float CLAHE would need a
/// binning policy (range + bin count); deferred.</para>
/// </summary>
public class VipsHistLocal : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    /// <summary>Tile grid size on each axis. Default 8 → 8×8 tiles.</summary>
    public int TileGridSize { get; set; } = 8;
    /// <summary>Clip multiplier in units of mean-bin-count. Higher = more contrast amplification (and noise).</summary>
    public double ClipLimit { get; set; } = 3.0;

    public override int Build()
    {
        if (In == null) return -1;
        if (In.BandFormat != VipsBandFormat.UChar) return -1;
        if (TileGridSize < 1) return -1;
        if (ClipLimit < 1.0) return -1;

        // Pre-compute the per-tile lookup tables once. Build is cheap enough
        // (one materializing scan + tileGrid² histograms); the result is a
        // 256 × tileGrid² byte LUT consumed by Generate via interpolation.
        var lut = ComputeClaheLuts(In, TileGridSize, ClipLimit);

        Out = new VipsImage
        {
            Width = In.Width, Height = In.Height, Bands = In.Bands,
            BandFormat = In.BandFormat, Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = lut
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("HistLocal", RuntimeHelpers.GetHashCode(In), TileGridSize, ClipLimit);

    /// <summary>
    /// Carries the per-tile CDFs plus the geometry needed to interpolate
    /// between them. Indexed as <c>luts[tileY * tileGridSize + tileX][band, value]</c>.
    /// </summary>
    private sealed class ClaheLuts
    {
        public int TileGridSize;
        public int Bands;
        /// <summary>One LUT per (tileY, tileX) per band. <c>[(ty * G + tx) * Bands + bnd][value]</c>.</summary>
        public byte[][] Luts = Array.Empty<byte[]>();
        public int Width;
        public int Height;
    }

    /// <summary>
    /// Materialise the input, walk every tile, build a clipped+redistributed
    /// CDF per band per tile. Caller stashes the result as ClientB so
    /// Generate can read without re-materialising.
    /// </summary>
    private static ClaheLuts ComputeClaheLuts(VipsImage input, int G, double clipLimit)
    {
        // Materialise the source pixels — CLAHE needs full-image access for
        // its tile histograms.
        byte[] pixels;
        if (input.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(input);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        int W = input.Width, H = input.Height;
        int bands = input.Bands;
        var luts = new byte[G * G * bands][];

        for (int ty = 0; ty < G; ty++)
        {
            int y0 = (int)((long)ty * H / G);
            int y1 = (int)((long)(ty + 1) * H / G);
            if (y1 > H) y1 = H;
            for (int tx = 0; tx < G; tx++)
            {
                int x0 = (int)((long)tx * W / G);
                int x1 = (int)((long)(tx + 1) * W / G);
                if (x1 > W) x1 = W;
                int tilePixels = (x1 - x0) * (y1 - y0);
                if (tilePixels == 0) continue;

                // One histogram per band for this tile.
                var hist = new int[bands, 256];
                for (int y = y0; y < y1; y++)
                {
                    int rowBase = y * W * bands;
                    for (int x = x0; x < x1; x++)
                    {
                        int pelBase = rowBase + x * bands;
                        for (int bnd = 0; bnd < bands; bnd++)
                            hist[bnd, pixels[pelBase + bnd]]++;
                    }
                }

                for (int bnd = 0; bnd < bands; bnd++)
                {
                    var lut = new byte[256];
                    BuildClippedEqualizedLut(hist, bnd, tilePixels, clipLimit, lut);
                    luts[(ty * G + tx) * bands + bnd] = lut;
                }
            }
        }

        return new ClaheLuts
        {
            TileGridSize = G,
            Bands = bands,
            Luts = luts,
            Width = W,
            Height = H,
        };
    }

    /// <summary>
    /// Apply the contrast-limit step (clip every bin at
    /// <c>clipLimit × meanBinCount</c>, redistribute the clipped excess
    /// uniformly across all bins) then build the cumulative LUT scaled to
    /// 0..255.
    /// </summary>
    private static void BuildClippedEqualizedLut(int[,] hist, int band, int tilePixels, double clipLimit, byte[] outLut)
    {
        int clipThreshold = (int)Math.Max(1, clipLimit * tilePixels / 256);
        var binCounts = new int[256];
        int excess = 0;
        for (int i = 0; i < 256; i++)
        {
            int c = hist[band, i];
            if (c > clipThreshold) { excess += c - clipThreshold; c = clipThreshold; }
            binCounts[i] = c;
        }
        // Redistribute excess uniformly. Some implementations do an
        // iterative redistribution that handles the case where the new
        // bin counts overflow the threshold again; the single-pass version
        // is the more common one and produces visually equivalent results.
        int distribute = excess / 256;
        int leftover = excess % 256;
        for (int i = 0; i < 256; i++) binCounts[i] += distribute;
        for (int i = 0; i < leftover; i++) binCounts[i]++;

        // Build cumulative LUT scaled to 0..255.
        int cum = 0;
        for (int i = 0; i < 256; i++)
        {
            cum += binCounts[i];
            outLut[i] = (byte)Math.Clamp((long)cum * 255 / Math.Max(1, tilePixels), 0, 255);
        }
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        var luts = (ClaheLuts)b!;
        VipsRect r = outRegion.Valid;

        if (inRegion.Prepare(r) != 0) return -1;

        int W = luts.Width, H = luts.Height;
        int bands = luts.Bands;
        int G = luts.TileGridSize;

        // Tile centres, in image coordinates. A pixel at (x, y) is bilinearly
        // interpolated between the four tile centres surrounding it.
        // Tile k spans [k*W/G, (k+1)*W/G); centre is the midpoint.
        // Pixels near the image edge fall outside any "between four centres"
        // region and use clamped tile indices — the standard CLAHE convention.

        for (int y = 0; y < r.Height; y++)
        {
            int gy = r.Top + y;
            // Find which two rows of tiles surround this pixel.
            // Centre of tile ty lies at: cy(ty) = ((2*ty + 1) * H) / (2*G).
            // Solve for ty such that cy(ty) ≤ gy < cy(ty + 1).
            double tyf = (gy * 2.0 * G - H) / (2.0 * H) * 1.0; // = (gy / H) * G - 0.5
            tyf = (gy * (double)G) / H - 0.5;
            int ty0 = (int)Math.Floor(tyf);
            int ty1 = ty0 + 1;
            double fy = tyf - ty0;
            ty0 = Math.Clamp(ty0, 0, G - 1);
            ty1 = Math.Clamp(ty1, 0, G - 1);

            var inAddr = inRegion.GetAddress(r.Left, gy);
            var outAddr = outRegion.GetAddress(r.Left, gy);

            for (int x = 0; x < r.Width; x++)
            {
                int gx = r.Left + x;
                double txf = (gx * (double)G) / W - 0.5;
                int tx0 = (int)Math.Floor(txf);
                int tx1 = tx0 + 1;
                double fx = txf - tx0;
                tx0 = Math.Clamp(tx0, 0, G - 1);
                tx1 = Math.Clamp(tx1, 0, G - 1);

                int pelOff = x * bands;
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    byte v = inAddr[pelOff + bnd];
                    // Look up v in each of the 4 surrounding tiles' LUTs.
                    var lut00 = luts.Luts[(ty0 * G + tx0) * bands + bnd];
                    var lut10 = luts.Luts[(ty0 * G + tx1) * bands + bnd];
                    var lut01 = luts.Luts[(ty1 * G + tx0) * bands + bnd];
                    var lut11 = luts.Luts[(ty1 * G + tx1) * bands + bnd];

                    double v00 = lut00[v];
                    double v10 = lut10[v];
                    double v01 = lut01[v];
                    double v11 = lut11[v];

                    // Bilinear blend.
                    double top = v00 * (1 - fx) + v10 * fx;
                    double bot = v01 * (1 - fx) + v11 * fx;
                    double final = top * (1 - fy) + bot * fy;
                    outAddr[pelOff + bnd] = (byte)Math.Clamp(final, 0, 255);
                }
            }
        }
        return 0;
    }
}
