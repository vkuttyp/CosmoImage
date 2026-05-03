using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Render a histogram as a bar-chart image. Input is a 1-row
/// histogram (typically the output of <c>HistFind</c>); output is a
/// (<c>Width</c>, <see cref="Height"/>) image where bin <c>i</c>
/// produces a vertical bar of height proportional to that bin's
/// count, scaled so the tallest bar fills the canvas. Mirrors libvips
/// <c>vips_hist_plot</c>.
///
/// <para>UChar single-band output. Multi-band histograms are handled
/// per-band: the output has the same band count, with each band's
/// bars rendered independently.</para>
/// </summary>
public class VipsHistPlot : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    /// <summary>Output image height. Default 256.</summary>
    public int Height { get; set; } = 256;

    public override int Build()
    {
        if (In == null) return -1;
        if (In.Height != 1) return -1;
        if (Height < 1) return -1;

        // Materialise input — typically tiny (256 wide), so cheap.
        byte[] pixels;
        if (In.Pixels is { } existing) pixels = existing;
        else
        {
            var sink = new MemorySink(In);
            sink.RunAsync().GetAwaiter().GetResult();
            pixels = sink.Pixels;
        }

        int W = In.Width, bands = In.Bands;
        var values = new double[W * bands];
        ReadHist(In, pixels, W, bands, values);

        // Per-band max for normalisation.
        var maxes = new double[bands];
        for (int x = 0; x < W; x++)
            for (int bnd = 0; bnd < bands; bnd++)
                if (values[x * bands + bnd] > maxes[bnd]) maxes[bnd] = values[x * bands + bnd];

        // Render bars: white if pixel y < bar height, else black.
        var outBuf = new byte[W * Height * bands];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < W; x++)
            {
                for (int bnd = 0; bnd < bands; bnd++)
                {
                    double frac = maxes[bnd] > 0 ? values[x * bands + bnd] / maxes[bnd] : 0;
                    int barTop = Height - (int)Math.Round(frac * Height);
                    outBuf[(y * W + x) * bands + bnd] = (byte)(y >= barTop ? 255 : 0);
                }
            }
        }

        Out = new VipsImage
        {
            Width = W, Height = Height, Bands = bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            Coding = In.Coding, XRes = 1, YRes = 1,
            StartFn = VipsSeq.StartOne, GenerateFn = Generate, StopFn = VipsSeq.StopOne,
            ClientA = In, ClientB = outBuf,
        };
        Out.CopyMetadataFrom(In);
        Out.SetPipeline(VipsDemandStyle.Any, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("HistPlot", RuntimeHelpers.GetHashCode(In), Height);

    /// <summary>
    /// Read the histogram into a flat per-band array. Histograms are
    /// commonly stored as UChar, UInt, or Float; handle whichever the
    /// input uses.
    /// </summary>
    private static void ReadHist(VipsImage @in, byte[] pixels, int W, int bands, double[] values)
    {
        switch (@in.BandFormat)
        {
            case VipsBandFormat.UChar:
                for (int x = 0; x < W; x++)
                    for (int bnd = 0; bnd < bands; bnd++)
                        values[x * bands + bnd] = pixels[x * bands + bnd];
                break;
            case VipsBandFormat.UInt:
                for (int x = 0; x < W; x++)
                    for (int bnd = 0; bnd < bands; bnd++)
                        values[x * bands + bnd] =
                            BinaryPrimitives.ReadUInt32LittleEndian(
                                pixels.AsSpan((x * bands + bnd) * 4, 4));
                break;
            case VipsBandFormat.Float:
                for (int x = 0; x < W; x++)
                    for (int bnd = 0; bnd < bands; bnd++)
                        values[x * bands + bnd] =
                            BinaryPrimitives.ReadSingleLittleEndian(
                                pixels.AsSpan((x * bands + bnd) * 4, 4));
                break;
            default:
                throw new ArgumentException("HistPlot supports UChar / UInt / Float histograms.");
        }
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var buf = (byte[])b!;
        VipsImage @out = outRegion.Image;
        VipsRect r = outRegion.Valid;
        int W = @out.Width;
        int bands = @out.Bands;
        for (int y = 0; y < r.Height; y++)
        {
            var addr = outRegion.GetAddress(r.Left, r.Top + y);
            int srcOff = ((r.Top + y) * W + r.Left) * bands;
            buf.AsSpan(srcOff, r.Width * bands).CopyTo(addr);
        }
        return 0;
    }
}
