using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Coverage for the pure-C# Netpbm parser/encoder. The existing
/// Round2Tests.Pnm_AutoVariant_ForRgb_WritesPpm round-trip exercises the
/// P6 binary path; these tests target the variants only the pure-C#
/// implementation supports — ASCII variants (P1/P2/P3) and the binary
/// bitmap (P4) — and the PNM-specific quirks like # comments and
/// non-255 maxval scaling.
/// </summary>
public class PureCSharpPnmTests
{
    private static IVipsSource SourceFromBytes(byte[] bytes)
        => new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));

    private static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    [Fact]
    public async Task P1_AsciiBitmap_LoadsCorrectly()
    {
        // 4x2 PBM with one black bit at (1, 0). PBM convention: 1 = black.
        // Our pipeline convention: 0 = black, 255 = white. So the loader
        // should invert.
        var pbm = Ascii("P1\n4 2\n0 1 0 0\n0 0 0 0\n");
        var img = await VipsPnmLoader.LoadAsync(SourceFromBytes(pbm));
        Assert.NotNull(img);
        Assert.Equal(4, img!.Width);
        Assert.Equal(2, img.Height);
        Assert.Equal(1, img.Bands);

        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 4, 2));
        Assert.Equal(255, reg.GetAddress(0, 0)[0]); // white
        Assert.Equal(0, reg.GetAddress(1, 0)[0]);   // black (inverted)
        Assert.Equal(255, reg.GetAddress(0, 1)[0]); // white
    }

    [Fact]
    public async Task P2_AsciiGrayscale_ScalesByMaxval()
    {
        // PGM ASCII with maxval=10 and a 2x1 image of values 5 and 10.
        // Should scale to 5*255/10 = 127, 10*255/10 = 255.
        var pgm = Ascii("P2\n2 1\n10\n5 10\n");
        var img = await VipsPnmLoader.LoadAsync(SourceFromBytes(pgm));
        Assert.NotNull(img);
        using var reg = new VipsRegion(img!);
        reg.Prepare(new VipsRect(0, 0, 2, 1));
        Assert.Equal(127, reg.GetAddress(0, 0)[0]);
        Assert.Equal(255, reg.GetAddress(1, 0)[0]);
    }

    [Fact]
    public async Task P3_AsciiRgb_HandlesAllBands()
    {
        // 1x1 PPM with R=10 G=200 B=50, maxval=255.
        var ppm = Ascii("P3\n1 1\n255\n10 200 50\n");
        var img = await VipsPnmLoader.LoadAsync(SourceFromBytes(ppm));
        Assert.NotNull(img);
        Assert.Equal(3, img!.Bands);
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 1, 1));
        Assert.Equal(10, reg.GetAddress(0, 0)[0]);
        Assert.Equal(200, reg.GetAddress(0, 0)[1]);
        Assert.Equal(50, reg.GetAddress(0, 0)[2]);
    }

    [Fact]
    public async Task P4_BinaryBitmap_PacksBitsCorrectly()
    {
        // 9x1 PBM (P4) — 9 bits, 2 bytes (second byte has 1 valid bit + padding).
        // Bit pattern 101001011 (msb first): byte0=0xA5 (10100101), byte1=0x80 (1xxxxxxx).
        var header = Ascii("P4\n9 1\n");
        var pixels = new byte[] { 0xA5, 0x80 };
        var pbm = new byte[header.Length + pixels.Length];
        System.Buffer.BlockCopy(header, 0, pbm, 0, header.Length);
        System.Buffer.BlockCopy(pixels, 0, pbm, header.Length, pixels.Length);

        var img = await VipsPnmLoader.LoadAsync(SourceFromBytes(pbm));
        Assert.NotNull(img);
        using var reg = new VipsRegion(img!);
        reg.Prepare(new VipsRect(0, 0, 9, 1));
        // Inverted: bit=1 → 0 (black), bit=0 → 255 (white).
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.Equal(255, reg.GetAddress(1, 0)[0]);
        Assert.Equal(0, reg.GetAddress(2, 0)[0]);
        Assert.Equal(255, reg.GetAddress(3, 0)[0]);
        Assert.Equal(255, reg.GetAddress(4, 0)[0]);
        Assert.Equal(0, reg.GetAddress(5, 0)[0]);
        Assert.Equal(255, reg.GetAddress(6, 0)[0]);
        Assert.Equal(0, reg.GetAddress(7, 0)[0]);
        Assert.Equal(0, reg.GetAddress(8, 0)[0]);
    }

    [Fact]
    public async Task Comments_AreSkippedInHeaderAndData()
    {
        // ASCII PGM with a # comment between header and data.
        var pgm = Ascii("P2\n# this is a comment\n2 2\n255\n10 20\n30 40\n");
        var img = await VipsPnmLoader.LoadAsync(SourceFromBytes(pgm));
        Assert.NotNull(img);
        Assert.Equal(2, img!.Width);
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(10, reg.GetAddress(0, 0)[0]);
        Assert.Equal(40, reg.GetAddress(1, 1)[0]);
    }

    [Fact]
    public async Task BinaryWithMaxvalUnder255_ScalesUp()
    {
        // P5 binary grayscale with maxval=100. A stored 50 should decode to ~127.
        var header = Ascii("P5\n2 1\n100\n");
        var pixels = new byte[] { 50, 100 };
        var pgm = new byte[header.Length + pixels.Length];
        System.Buffer.BlockCopy(header, 0, pgm, 0, header.Length);
        System.Buffer.BlockCopy(pixels, 0, pgm, header.Length, pixels.Length);

        var img = await VipsPnmLoader.LoadAsync(SourceFromBytes(pgm));
        Assert.NotNull(img);
        using var reg = new VipsRegion(img!);
        reg.Prepare(new VipsRect(0, 0, 2, 1));
        Assert.Equal(127, reg.GetAddress(0, 0)[0]);
        Assert.Equal(255, reg.GetAddress(1, 0)[0]);
    }

    [Fact]
    public async Task PbmSave_BinarizesAndPacks()
    {
        // 1-band image with values that straddle the midpoint threshold.
        var src = new VipsImage
        {
            Width = 4, Height = 1, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                var addr = reg.GetAddress(0, 0);
                addr[0] = 0;     // black
                addr[1] = 100;   // black
                addr[2] = 200;   // white
                addr[3] = 255;   // white
                return 0;
            }
        };

        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms, new StreamPipeWriterOptions(leaveOpen: true));
        await VipsPnmSaver.SaveAsync(src, writer, VipsPnmVariant.Pbm);

        var bytes = ms.ToArray();
        Assert.Equal((byte)'P', bytes[0]);
        Assert.Equal((byte)'4', bytes[1]);

        // Reload via our own parser and verify the round-trip.
        var loaded = await VipsPnmLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        using var reg = new VipsRegion(loaded!);
        reg.Prepare(new VipsRect(0, 0, 4, 1));
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);   // 0 → black
        Assert.Equal(0, reg.GetAddress(1, 0)[0]);   // 100 < 128 → black
        Assert.Equal(255, reg.GetAddress(2, 0)[0]); // 200 ≥ 128 → white
        Assert.Equal(255, reg.GetAddress(3, 0)[0]); // 255 → white
    }

    [Fact]
    public async Task PgmSave_FromRgbInput_ConvertsToLuminance()
    {
        // Pure red → Rec.709 luminance ≈ 54.
        var src = new VipsImage
        {
            Width = 1, Height = 1, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                var addr = reg.GetAddress(0, 0);
                addr[0] = 255; addr[1] = 0; addr[2] = 0;
                return 0;
            }
        };

        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms, new StreamPipeWriterOptions(leaveOpen: true));
        await VipsPnmSaver.SaveAsync(src, writer, VipsPnmVariant.Pgm);
        var bytes = ms.ToArray();
        Assert.Equal((byte)'5', bytes[1]); // P5

        var loaded = await VipsPnmLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        using var reg = new VipsRegion(loaded!);
        reg.Prepare(new VipsRect(0, 0, 1, 1));
        Assert.InRange(reg.GetAddress(0, 0)[0], 53, 55);
    }

    [Fact]
    public async Task P6_RgbBinary_RoundTripsExactly()
    {
        var src = new VipsImage
        {
            Width = 4, Height = 4, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        addr[x * 3] = (byte)(x * 60);
                        addr[x * 3 + 1] = (byte)(y * 60);
                        addr[x * 3 + 2] = 100;
                    }
                }
                return 0;
            }
        };

        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms, new StreamPipeWriterOptions(leaveOpen: true));
        await VipsPnmSaver.SaveAsync(src, writer, VipsPnmVariant.Ppm);
        var bytes = ms.ToArray();

        var loaded = await VipsPnmLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        using var reg = new VipsRegion(loaded!);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        // Spot-check (2, 3): R = 2*60 = 120, G = 3*60 = 180, B = 100.
        Assert.Equal(120, reg.GetAddress(2, 3)[0]);
        Assert.Equal(180, reg.GetAddress(2, 3)[1]);
        Assert.Equal(100, reg.GetAddress(2, 3)[2]);
    }

    [Fact]
    public async Task NonPnmInput_ReturnsNull()
    {
        var notPnm = Ascii("PNG\r\nnot PNM");
        var result = await VipsPnmLoader.LoadAsync(SourceFromBytes(notPnm));
        Assert.Null(result);
    }
}
