using System;
using Xunit;

namespace CosmoImage.Tests;

public class TypedImageTests
{
    [Fact]
    public void NewRgba32_FreshBuffer_AllPixelsZero()
    {
        var t = new TypedImage<Rgba32>(4, 4);
        var px = t[0, 0];
        Assert.Equal(0, px.R);
        Assert.Equal(0, px.G);
        Assert.Equal(0, px.B);
        Assert.Equal(0, px.A);
    }

    [Fact]
    public void Indexer_GetSet_RoundTripsValue()
    {
        var t = new TypedImage<Rgba32>(8, 8);
        t[3, 5] = new Rgba32(10, 20, 30, 40);
        var px = t[3, 5];
        Assert.Equal(10, px.R);
        Assert.Equal(20, px.G);
        Assert.Equal(30, px.B);
        Assert.Equal(40, px.A);

        // Other pixels untouched.
        var other = t[0, 0];
        Assert.Equal(0, other.R);
    }

    [Fact]
    public void RowSpan_RefForeach_MutatesInPlace()
    {
        var t = new TypedImage<Rgb24>(4, 2);
        foreach (ref Rgb24 px in t.RowSpan(0))
            px.R = 200;

        for (int x = 0; x < 4; x++)
        {
            Assert.Equal(200, t[x, 0].R);
            Assert.Equal(0, t[x, 1].R); // row 1 untouched
        }
    }

    [Fact]
    public void RowSpan_OutOfRange_Throws()
    {
        var t = new TypedImage<L8>(2, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => t.RowSpan(2));
    }

    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        var t = new TypedImage<L8>(2, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => t[2, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => t[0, 2]);
    }

    [Fact]
    public void AsVipsImage_AliasesUnderlyingBuffer_OpsSeeMutations()
    {
        var t = new TypedImage<L8>(2, 2);
        t[0, 0] = new L8(100);
        t[1, 0] = new L8(150);
        t[0, 1] = new L8(200);
        t[1, 1] = new L8(250);

        var vips = t.AsVipsImage();
        var inverted = vips.Invert();

        using var reg = new VipsRegion(inverted);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        // Invert is 255 - x.
        Assert.Equal(155, reg.GetAddress(0, 0)[0]);
        Assert.Equal(105, reg.GetAddress(1, 0)[0]);
        Assert.Equal(55, reg.GetAddress(0, 1)[0]);
        Assert.Equal(5, reg.GetAddress(1, 1)[0]);
    }

    [Fact]
    public void ToTypedImage_FromVipsImage_MaterializesCorrectly()
    {
        var src = new VipsImage
        {
            Width = 3, Height = 2, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        addr[x * 3 + 0] = (byte)(reg.Valid.Left + x + 1);
                        addr[x * 3 + 1] = 50;
                        addr[x * 3 + 2] = (byte)((reg.Valid.Top + y) * 10);
                    }
                }
                return 0;
            }
        };
        var typed = src.ToTypedImage<Rgb24>();

        Assert.Equal(3, typed.Width);
        Assert.Equal(2, typed.Height);
        Assert.Equal(1, typed[0, 0].R);
        Assert.Equal(50, typed[0, 0].G);
        Assert.Equal(0, typed[0, 0].B);
        Assert.Equal(3, typed[2, 0].R);
        Assert.Equal(10, typed[0, 1].B);
    }

    [Fact]
    public void ToTypedImage_BandMismatch_Throws()
    {
        var src = new VipsImage { Width = 2, Height = 2, Bands = 1, BandFormat = VipsBandFormat.UChar };
        var ex = Assert.Throws<InvalidOperationException>(() => src.ToTypedImage<Rgba32>());
        Assert.Contains("4 band", ex.Message);
    }

    [Fact]
    public void ToTypedImage_FormatMismatch_Throws()
    {
        var src = new VipsImage { Width = 2, Height = 2, Bands = 1, BandFormat = VipsBandFormat.Float };
        Assert.Throws<InvalidOperationException>(() => src.ToTypedImage<L8>());
    }

    [Fact]
    public void GetPixel_ConvenienceShortcut_Works()
    {
        var t = new TypedImage<L8>(2, 2);
        t[1, 1] = new L8(77);
        var vips = t.AsVipsImage();
        Assert.Equal(77, vips.GetPixel<L8>(1, 1).L);
    }

    [Fact]
    public void TypedImage_Metadata_RoundTripsThroughAsVipsImage()
    {
        var t = new TypedImage<L8>(2, 2);
        t.Metadata["comment"] = "hello";
        t.MetadataBlobs["xmp"] = new byte[] { 1, 2, 3 };

        var vips = t.AsVipsImage();
        Assert.Equal("hello", vips.GetComment());
        Assert.Equal(new byte[] { 1, 2, 3 }, vips.GetXmp());
    }

    [Fact]
    public void IPixel_StaticAbstracts_ReportCorrectShape()
    {
        Assert.Equal(1, L8.BandCount);
        Assert.Equal(2, La16.BandCount);
        Assert.Equal(3, Rgb24.BandCount);
        Assert.Equal(4, Rgba32.BandCount);
        Assert.Equal(VipsBandFormat.UChar, Rgba32.BandFormat);
    }
}
