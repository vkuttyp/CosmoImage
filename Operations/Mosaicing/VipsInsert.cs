using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Mosaicing;

/// <summary>
/// Paste a sub-image into a base at a given (x, y). Mirrors libvips
/// <c>vips_insert</c>.
///
/// <para>Default <c>Expand = false</c>: output is the same size as
/// the base, and any sub pixels falling outside the base are dropped.
/// <c>Expand = true</c>: the output bounding-box is the union of the
/// two image rectangles, and pixels not covered by either input are
/// filled with <see cref="Background"/>. Sub wins on overlap; that's
/// the libvips convention.</para>
/// </summary>
public class VipsInsert : VipsOperation
{
    public VipsImage? Base { get; set; }
    public VipsImage? Sub { get; set; }
    public VipsImage? Out { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public bool Expand { get; set; } = false;
    public double[]? Background { get; set; }

    public override int Build()
    {
        if (Base == null || Sub == null) return -1;
        if (Base.Bands != Sub.Bands || Base.BandFormat != Sub.BandFormat) return -1;

        int outW, outH, baseX, baseY, subX, subY;
        if (!Expand)
        {
            outW = Base.Width; outH = Base.Height;
            baseX = 0; baseY = 0;
            subX = X; subY = Y;
        }
        else
        {
            int left = Math.Min(0, X);
            int top = Math.Min(0, Y);
            int right = Math.Max(Base.Width, X + Sub.Width);
            int bottom = Math.Max(Base.Height, Y + Sub.Height);
            outW = right - left;
            outH = bottom - top;
            baseX = -left; baseY = -top;
            subX = X - left; subY = Y - top;
        }

        Out = new VipsImage
        {
            Width = outW, Height = outH, Bands = Base.Bands, BandFormat = Base.BandFormat,
            Interpretation = Base.Interpretation,
            Coding = Base.Coding, XRes = Base.XRes, YRes = Base.YRes,
            StartFn = VipsSeq.StartMany, GenerateFn = Generate, StopFn = VipsSeq.StopMany,
            ClientA = new[] { Base, Sub },
            ClientB = (baseX, baseY, subX, subY, Background ?? new double[Base.Bands]),
        };
        Out.CopyMetadataFrom(Base);
        Out.SetPipeline(VipsDemandStyle.Any, Base, Sub);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Insert", RuntimeHelpers.GetHashCode(Base),
            RuntimeHelpers.GetHashCode(Sub), X, Y, Expand);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var regions = (VipsRegion[])seq!;
        var (baseX, baseY, subX, subY, background) =
            ((int, int, int, int, double[]))b!;
        VipsImage outImg = outRegion.Image;
        VipsImage baseImg = regions[0].Image;
        VipsImage subImg = regions[1].Image;
        VipsRect r = outRegion.Valid;
        bool isFloat = outImg.BandFormat == VipsBandFormat.Float;
        int sampleSize = isFloat ? 4 : 1;
        int bands = outImg.Bands;
        int pelBytes = bands * sampleSize;

        // Background fill first.
        var bgPel = new byte[pelBytes];
        if (isFloat)
        {
            for (int bnd = 0; bnd < bands; bnd++)
                BinaryPrimitives.WriteSingleLittleEndian(
                    bgPel.AsSpan(bnd * 4, 4), (float)background[bnd]);
        }
        else
        {
            for (int bnd = 0; bnd < bands; bnd++)
                bgPel[bnd] = (byte)Math.Clamp(background[bnd], 0, 255);
        }
        for (int y = 0; y < r.Height; y++)
        {
            var addr = outRegion.GetAddress(r.Left, r.Top + y);
            for (int x = 0; x < r.Width; x++)
                bgPel.AsSpan().CopyTo(addr.Slice(x * pelBytes, pelBytes));
        }

        // Base first, then sub on top — libvips semantics.
        if (CopyOverlap(regions[0], outRegion, baseImg, baseX, baseY, r, pelBytes) != 0) return -1;
        if (CopyOverlap(regions[1], outRegion, subImg, subX, subY, r, pelBytes) != 0) return -1;
        return 0;
    }

    private static int CopyOverlap(VipsRegion src, VipsRegion dst, VipsImage img,
        int ox, int oy, VipsRect r, int pelBytes)
    {
        int x0 = Math.Max(r.Left, ox);
        int y0 = Math.Max(r.Top, oy);
        int x1 = Math.Min(r.Left + r.Width, ox + img.Width);
        int y1 = Math.Min(r.Top + r.Height, oy + img.Height);
        if (x0 >= x1 || y0 >= y1) return 0;
        var inRect = new VipsRect(x0 - ox, y0 - oy, x1 - x0, y1 - y0);
        if (src.Prepare(inRect) != 0) return -1;
        int rowBytes = inRect.Width * pelBytes;
        for (int sy = 0; sy < inRect.Height; sy++)
        {
            var inAddr = src.GetAddress(inRect.Left, inRect.Top + sy);
            var outAddr = dst.GetAddress(x0, y0 + sy);
            inAddr.Slice(0, rowBytes).CopyTo(outAddr);
        }
        return 0;
    }
}
