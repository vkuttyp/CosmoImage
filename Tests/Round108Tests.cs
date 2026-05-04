using System;
using System.IO;
using System.Threading.Tasks;
using CosmoImage.Core;
using Xunit;

namespace CosmoImage.Tests;

[Collection("VipsConfiguration")]
public class Round108Tests : IDisposable
{
    public Round108Tests() => VipsConfiguration.Default.Reset();
    public void Dispose() => VipsConfiguration.Default.Reset();

    private static VipsImage Solid(int w, int h, byte r, byte g, byte b)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? bb, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        addr[x * 3 + 0] = r;
                        addr[x * 3 + 1] = g;
                        addr[x * 3 + 2] = b;
                    }
                }
                return 0;
            }
        };

    // ---- Built-in extension tables ----

    [Theory]
    [InlineData("PNG", ".png")]
    [InlineData("JPEG", ".jpg")]
    [InlineData("JPEG", ".jpeg")]
    [InlineData("WEBP", ".webp")]
    [InlineData("GIF", ".gif")]
    [InlineData("BMP", ".bmp")]
    [InlineData("TIFF", ".tif")]
    [InlineData("TIFF", ".tiff")]
    [InlineData("QOI", ".qoi")]
    [InlineData("HEIF", ".heif")]
    [InlineData("HEIF", ".heic")]
    [InlineData("HEIF", ".avif")]
    [InlineData("HDR", ".hdr")]
    [InlineData("FITS", ".fits")]
    [InlineData("NIFTI", ".nii")]
    [InlineData("MAT", ".mat")]
    [InlineData("PNM", ".pnm")]
    [InlineData("TGA", ".tga")]
    [InlineData("PDF", ".pdf")]
    [InlineData("SVG", ".svg")]
    [InlineData("JXL", ".jxl")]
    [InlineData("JP2K", ".jp2")]
    public void FindByExtension_KnownExtensionsResolveToCorrectFormat(string formatName, string ext)
    {
        var fmt = VipsConfiguration.Default.FindByExtension(ext);
        Assert.NotNull(fmt);
        Assert.Equal(formatName, fmt!.Name);
    }

    [Fact]
    public void FindByExtension_CaseInsensitive()
    {
        Assert.Equal("PNG", VipsConfiguration.Default.FindByExtension(".PNG")!.Name);
        Assert.Equal("PNG", VipsConfiguration.Default.FindByExtension(".Png")!.Name);
    }

    [Fact]
    public void FindByExtension_LeadingDotOptional()
    {
        Assert.Equal("PNG", VipsConfiguration.Default.FindByExtension("png")!.Name);
        Assert.Equal("PNG", VipsConfiguration.Default.FindByExtension(".png")!.Name);
    }

    [Fact]
    public void FindByExtension_Unknown_ReturnsNull()
    {
        Assert.Null(VipsConfiguration.Default.FindByExtension(".xyz"));
        Assert.Null(VipsConfiguration.Default.FindByExtension(""));
        Assert.Null(VipsConfiguration.Default.FindByExtension(null!));
    }

    // ---- CanEncode + FileExtensions on built-ins ----

    [Fact]
    public void BuiltIn_PngCanEncode()
    {
        var png = VipsConfiguration.Default.FindByName("PNG");
        Assert.NotNull(png);
        Assert.True(png!.CanEncode);
        Assert.Contains(".png", png.FileExtensions);
    }

    [Fact]
    public void BuiltIn_JxlCannotEncode()
    {
        // JXL is decoder-header-only; no saver was wired for it.
        var jxl = VipsConfiguration.Default.FindByName("JXL");
        Assert.NotNull(jxl);
        Assert.False(jxl!.CanEncode);
    }

    [Fact]
    public void BuiltIn_PdfAndSvgAreLoadOnly()
    {
        var pdf = VipsConfiguration.Default.FindByName("PDF");
        Assert.NotNull(pdf);
        Assert.False(pdf!.CanEncode);
        var svg = VipsConfiguration.Default.FindByName("SVG");
        Assert.NotNull(svg);
        Assert.False(svg!.CanEncode);
    }

    // ---- SaveByExtensionAsync end-to-end ----

    [Fact]
    public async Task SaveByExtension_Png_RoundTrips()
    {
        var src = Solid(8, 8, 200, 100, 50);
        using var ms = new MemoryStream();
        await VipsConfiguration.Default.SaveByExtensionAsync(src, ms, ".png");
        Assert.True(ms.Length > 0);
        // Output should start with the PNG signature.
        var bytes = ms.ToArray();
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);
    }

    [Fact]
    public async Task SaveByExtension_Jpeg_ProducesJpegBytes()
    {
        var src = Solid(8, 8, 100, 200, 150);
        using var ms = new MemoryStream();
        await VipsConfiguration.Default.SaveByExtensionAsync(src, ms, ".jpg");
        var bytes = ms.ToArray();
        // JPEG magic: FF D8 FF.
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
        Assert.Equal(0xFF, bytes[2]);
    }

    [Fact]
    public async Task SaveByExtension_Bmp_ProducesBmpBytes()
    {
        var src = Solid(4, 4, 50, 150, 250);
        using var ms = new MemoryStream();
        await VipsConfiguration.Default.SaveByExtensionAsync(src, ms, ".bmp");
        var bytes = ms.ToArray();
        Assert.Equal((byte)'B', bytes[0]);
        Assert.Equal((byte)'M', bytes[1]);
    }

    [Fact]
    public async Task SaveByExtension_UnknownExtension_Throws()
    {
        var src = Solid(4, 4, 0, 0, 0);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await VipsConfiguration.Default.SaveByExtensionAsync(src, new MemoryStream(), ".xyz"));
    }

    [Fact]
    public async Task SaveByExtension_DecoderOnlyFormat_Throws()
    {
        // PDF is decoder-only — saving via .pdf extension should throw.
        var src = Solid(4, 4, 0, 0, 0);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await VipsConfiguration.Default.SaveByExtensionAsync(src, new MemoryStream(), ".pdf"));
    }

    [Fact]
    public async Task SaveByExtension_NullArgs_Throw()
    {
        var src = Solid(4, 4, 0, 0, 0);
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await VipsConfiguration.Default.SaveByExtensionAsync(null!, new MemoryStream(), ".png"));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await VipsConfiguration.Default.SaveByExtensionAsync(src, null!, ".png"));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await VipsConfiguration.Default.SaveByExtensionAsync(src, new MemoryStream(), null!));
    }

    // ---- Custom format with extensions ----

    private sealed class CustomFormatWithExtension : IVipsImageFormat
    {
        public string Name => "MYFMT";
        public IReadOnlyList<string> FileExtensions => new[] { ".myf", ".mfx" };
        public ValueTask<bool> CanDecodeAsync(IVipsSource source, System.Threading.CancellationToken ct = default)
            => ValueTask.FromResult(false);
        public ValueTask<VipsImage?> LoadAsync(IVipsSource source, System.Threading.CancellationToken ct = default)
            => ValueTask.FromResult<VipsImage?>(null);
    }

    [Fact]
    public void FindByExtension_CustomProvider_OverridesBuiltIn()
    {
        var custom = new CustomFormatWithExtension();
        VipsConfiguration.Default.Register(custom);
        Assert.Same(custom, VipsConfiguration.Default.FindByExtension(".myf"));
        Assert.Same(custom, VipsConfiguration.Default.FindByExtension(".mfx"));
    }
}
