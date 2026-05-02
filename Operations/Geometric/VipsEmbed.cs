using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Geometric;

public enum VipsExtend
{
    /// <summary>Out-of-source pixels are filled with zero (transparent black).</summary>
    Black = 0,
    /// <summary>Filled with white (255 / 1.0).</summary>
    White = 1,
    /// <summary>Replicate the nearest edge pixel.</summary>
    Copy = 2,
    /// <summary>Tile the source toroidally.</summary>
    Repeat = 3,
    /// <summary>Reflect across each edge.</summary>
    Mirror = 4,
    /// <summary>Filled with caller-supplied per-band background colour.</summary>
    Background = 5,
}

/// <summary>
/// Place an image at <c>(x, y)</c> within a larger <c>(width, height)</c>
/// canvas, filling the rest according to <see cref="Extend"/>. The canvas
/// dimensions can be smaller than the input + offset (in which case
/// `Embed` works as a crop) but the typical use is enlarging.
///
/// <para>Extension modes mirror libvips <c>vips_embed</c>:</para>
/// <list type="bullet">
///   <item><c>Black</c> / <c>White</c> — fill with constant 0 / 255 (1.0 on Float).</item>
///   <item><c>Copy</c> — replicate the nearest edge pixel (clamp-to-edge).</item>
///   <item><c>Repeat</c> — tile the source modulo input dimensions.</item>
///   <item><c>Mirror</c> — reflect across each edge.</item>
///   <item><c>Background</c> — fill with caller-supplied per-band colour
///     (<see cref="Background"/>).</item>
/// </list>
/// </summary>
public class VipsEmbed : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int OutWidth { get; set; }
    public int OutHeight { get; set; }
    public VipsExtend Extend { get; set; } = VipsExtend.Black;
    /// <summary>Per-band fill colour for <see cref="VipsExtend.Background"/>; ignored otherwise.</summary>
    public double[]? Background { get; set; }

    public override int Build()
    {
        if (In == null) return -1;
        if (OutWidth <= 0 || OutHeight <= 0) return -1;

        Out = new VipsImage
        {
            Width = OutWidth, Height = OutHeight, Bands = In.Bands,
            BandFormat = In.BandFormat, Interpretation = In.Interpretation,
            Coding = In.Coding, XRes = In.XRes, YRes = In.YRes,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In,
            ClientB = new { X, Y, Extend, Background, InWidth = In.Width, InHeight = In.Height }
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.SmallTile, In);
        return 0;
    }

    public override int GetCacheKey()
    {
        var h = new HashCode();
        h.Add("Embed");
        h.Add(RuntimeHelpers.GetHashCode(In));
        h.Add(X); h.Add(Y);
        h.Add(OutWidth); h.Add(OutHeight);
        h.Add(Extend);
        if (Background != null) foreach (var c in Background) h.Add(c);
        return h.ToHashCode();
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        dynamic config = b!;
        int ox = config.X;
        int oy = config.Y;
        VipsExtend extend = config.Extend;
        double[]? background = config.Background;
        int inW = config.InWidth;
        int inH = config.InHeight;
        VipsRect r = outRegion.Valid;

        int bands = @in.Bands;
        bool isFloat = @in.BandFormat == VipsBandFormat.Float;
        int bytesPerSample = isFloat ? 4 : 1;
        int rowBytes = r.Width * bands * bytesPerSample;

        // Compute the rect of the input that's visible in this tile.
        VipsRect inRectInOut = new VipsRect(ox, oy, inW, inH);
        VipsRect overlap = VipsRect.Intersect(r, inRectInOut);

        if (!overlap.IsEmpty)
        {
            // Pull only the input slice that maps into the tile. Coordinates
            // in the input frame are (out - offset).
            var inFetch = new VipsRect(overlap.Left - ox, overlap.Top - oy, overlap.Width, overlap.Height);
            if (inRegion.Prepare(inFetch) != 0) return -1;
        }

        // Fill the whole output tile pixel by pixel. Hot inner loop branches
        // on whether the pixel falls inside the input rect.
        for (int yi = 0; yi < r.Height; yi++)
        {
            int outY = r.Top + yi;
            int srcY = outY - oy;
            var dst = outRegion.GetAddress(r.Left, outY);

            for (int xi = 0; xi < r.Width; xi++)
            {
                int outX = r.Left + xi;
                int srcX = outX - ox;
                int dstOff = xi * bands * bytesPerSample;

                if ((uint)srcX < (uint)inW && (uint)srcY < (uint)inH)
                {
                    // Inside source rect — direct copy.
                    var srcAddr = inRegion.GetAddress(srcX, srcY);
                    srcAddr.Slice(0, bands * bytesPerSample).CopyTo(dst.Slice(dstOff, bands * bytesPerSample));
                }
                else
                {
                    FillExtensionPixel(dst.Slice(dstOff, bands * bytesPerSample), inRegion, srcX, srcY,
                        inW, inH, bands, isFloat, extend, background);
                }
            }
        }
        return 0;
    }

    private static void FillExtensionPixel(Span<byte> dst, VipsRegion inRegion,
        int srcX, int srcY, int inW, int inH, int bands, bool isFloat,
        VipsExtend extend, double[]? background)
    {
        int bytesPerSample = isFloat ? 4 : 1;

        switch (extend)
        {
            case VipsExtend.Black:
                dst.Clear();
                return;

            case VipsExtend.White:
                if (isFloat)
                    for (int i = 0; i < bands; i++)
                        BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(i * 4, 4), 1f);
                else
                    dst.Fill(255);
                return;

            case VipsExtend.Background:
                if (background == null) { dst.Clear(); return; }
                for (int i = 0; i < bands; i++)
                {
                    double v = background[i % background.Length];
                    if (isFloat)
                        BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(i * 4, 4), (float)v);
                    else
                        dst[i] = (byte)Math.Clamp(v, 0, 255);
                }
                return;

            case VipsExtend.Copy:
            {
                int cx = Math.Clamp(srcX, 0, inW - 1);
                int cy = Math.Clamp(srcY, 0, inH - 1);
                inRegion.GetAddress(cx, cy).Slice(0, bands * bytesPerSample).CopyTo(dst);
                return;
            }

            case VipsExtend.Repeat:
            {
                int cx = ((srcX % inW) + inW) % inW;
                int cy = ((srcY % inH) + inH) % inH;
                inRegion.GetAddress(cx, cy).Slice(0, bands * bytesPerSample).CopyTo(dst);
                return;
            }

            case VipsExtend.Mirror:
            {
                int cx = Mirror(srcX, inW);
                int cy = Mirror(srcY, inH);
                inRegion.GetAddress(cx, cy).Slice(0, bands * bytesPerSample).CopyTo(dst);
                return;
            }
        }
    }

    /// <summary>
    /// Reflect <paramref name="i"/> into <c>[0, n)</c> by mirroring at each
    /// edge. Period is <c>2n</c>: ..., -2, -1, 0, 1, 2, …, n-1, n-1, n-2, …
    /// matches the canonical "edge-doubled" mirror used in image processing
    /// (vs the simpler "no-edge-doubled" mirror with period 2n-2).
    /// </summary>
    private static int Mirror(int i, int n)
    {
        if (n <= 1) return 0;
        int period = 2 * n;
        int m = ((i % period) + period) % period;
        return m < n ? m : period - 1 - m;
    }
}
