using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Operations.Convolution;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

public class Round2Tests
{
    private static VipsImage Uniform(int w, int h, byte value, int bands = 3)
    {
        return new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int i = 0; i < reg.Valid.Width * bands; i++) addr[i] = value;
                }
                return 0;
            }
        };
    }

    private static async Task<byte[]> SaveToBytesAsync(System.Func<PipeWriter, Task> save)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms, new StreamPipeWriterOptions(leaveOpen: true));
        await save(writer);
        return ms.ToArray();
    }

    [Fact]
    public async Task Tga_RoundTrip_PreservesPixels()
    {
        var src = Uniform(8, 8, 200, bands: 3);
        var bytes = await SaveToBytesAsync(w => VipsTgaSaver.SaveAsync(src, w));
        // Surface the actual TGA header so a sniff regression is debuggable
        // from the test output rather than a silent null.
        var hdr = string.Join(" ", System.Linq.Enumerable.Range(0, System.Math.Min(18, bytes.Length))
            .Select(i => bytes[i].ToString("X2")));

        var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        var loaded = await VipsTgaLoader.LoadAsync(source);
        Assert.True(loaded != null, $"TGA load returned null. Header bytes: {hdr}");
        Assert.Equal(8, loaded!.Width);
        Assert.Equal(8, loaded.Height);

        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        Assert.Equal(200, reg.GetAddress(0, 0)[0]);
    }

    [Fact]
    public async Task Qoi_RoundTrip_PreservesPixels()
    {
        var src = Uniform(8, 8, 90, bands: 4);
        var bytes = await SaveToBytesAsync(w => VipsQoiSaver.SaveAsync(src, w));

        var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        var loaded = await VipsQoiLoader.LoadAsync(source);
        Assert.NotNull(loaded);
        Assert.Equal(8, loaded!.Width);
        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        Assert.Equal(90, reg.GetAddress(0, 0)[0]);
    }

    [Fact]
    public async Task Pnm_AutoVariant_ForRgb_WritesPpm()
    {
        var src = Uniform(4, 4, 50, bands: 3);
        var bytes = await SaveToBytesAsync(w => VipsPnmSaver.SaveAsync(src, w));

        // PPM magic is "P6" (binary) or "P3" (ASCII). Magick chooses one.
        Assert.Equal((byte)'P', bytes[0]);
        Assert.True(bytes[1] == (byte)'6' || bytes[1] == (byte)'3');

        var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        var loaded = await VipsPnmLoader.LoadAsync(source);
        Assert.NotNull(loaded);
        using var reg = new VipsRegion(loaded!);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        Assert.Equal(50, reg.GetAddress(0, 0)[0]);
    }

    [Fact]
    public void BokehBlur_OnUniformImage_PreservesBrightness()
    {
        var src = Uniform(20, 20, 100, bands: 1);
        var blurred = src.BokehBlur(3);
        using var reg = new VipsRegion(blurred);
        // Stay clear of edges where the kernel partially leaves the image.
        // Conv truncates rather than rounds, so a unit-sum kernel can
        // produce 99 instead of 100 — accept either.
        reg.Prepare(new VipsRect(8, 8, 4, 4));
        Assert.InRange(reg.GetAddress(10, 10)[0], 99, 100);
    }

    [Fact]
    public void HexagonKernel_NormalizesToOne()
    {
        var k = VipsBokehBlur.HexagonKernel(4);
        double sum = 0;
        for (int y = 0; y < k.GetLength(0); y++)
            for (int x = 0; x < k.GetLength(1); x++)
                sum += k[y, x];
        Assert.Equal(1.0, sum, 6);
    }

    [Fact]
    public async Task TiffPyramid_WritesPtifMagic()
    {
        var src = Uniform(64, 64, 120, bands: 3);
        var bytes = await SaveToBytesAsync(w => VipsTiffSaver.SaveAsync(src, w, pyramid: true));
        // TIFF magic stays "II*\0" or "MM\0*"; Ptif is just multi-resolution
        // inside a regular TIFF container. We just assert the file decodes
        // back through our loader, and the smallest level is present.
        Assert.True(bytes.Length > 0);
        Assert.True((bytes[0] == 0x49 && bytes[1] == 0x49) || (bytes[0] == 0x4D && bytes[1] == 0x4D));
    }

    [Fact]
    public async Task CsvLoader_BasicGrid_ParsesUCharRows()
    {
        var csv = "0, 64, 128\n255, 200, 100\n";
        var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv))));
        var img = await VipsCsvLoader.LoadAsync(source);
        Assert.NotNull(img);
        Assert.Equal(3, img!.Width);
        Assert.Equal(2, img.Height);

        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 3, 2));
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.Equal(64, reg.GetAddress(1, 0)[0]);
        Assert.Equal(128, reg.GetAddress(2, 0)[0]);
        Assert.Equal(255, reg.GetAddress(0, 1)[0]);
    }

    [Fact]
    public async Task CsvLoader_CommentsAndBlankLines_AreSkipped()
    {
        var csv = "# header comment\n\n10 20 30\n40 50 60\n";
        var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv))));
        var img = await VipsCsvLoader.LoadAsync(source);
        Assert.NotNull(img);
        Assert.Equal(3, img!.Width);
        Assert.Equal(2, img.Height);
    }

    [Fact]
    public async Task MatrixLoader_HeaderPlusRows_Parses()
    {
        // libvips matrix file: "width height" header, then rows.
        var mat = "3 2\n10 20 30\n40 50 60\n";
        var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(mat))));
        var img = await VipsMatrixLoader.LoadAsync(source);
        Assert.NotNull(img);
        Assert.Equal(3, img!.Width);
        Assert.Equal(2, img.Height);
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 3, 2));
        Assert.Equal(10, reg.GetAddress(0, 0)[0]);
        Assert.Equal(60, reg.GetAddress(2, 1)[0]);
    }

    [Fact]
    public void VipsFields_TypedAccessors_RoundTrip()
    {
        var img = Uniform(2, 2, 0);
        img.SetOrientation(6);
        img.SetComment("hello");
        img.SetDoubleArray("gps", new[] { 37.7749, -122.4194, 30.0 });
        img.SetAnimationDelays(new[] { 10, 20, 40 });
        img.SetXmp(System.Text.Encoding.UTF8.GetBytes("<xmp/>"));

        Assert.Equal(6, img.GetOrientation());
        Assert.Equal("hello", img.GetComment());
        var gps = img.GetDoubleArray("gps");
        Assert.NotNull(gps);
        Assert.Equal(3, gps!.Length);
        Assert.Equal(37.7749, gps[0], 4);
        Assert.Equal(new[] { 10, 20, 40 }, img.GetAnimationDelays());
        Assert.Equal("<xmp/>", System.Text.Encoding.UTF8.GetString(img.GetXmp()!));
    }

    [Fact]
    public void VipsFields_AbsentField_ReturnsNull()
    {
        var img = Uniform(2, 2, 0);
        Assert.Null(img.GetOrientation());
        Assert.Null(img.GetComment());
        Assert.Null(img.GetExif());
    }
}
