using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

public class OmeTiffTests
{
    private const string SampleOmeXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<OME xmlns=""http://www.openmicroscopy.org/Schemas/OME/2016-06"">
  <Image ID=""Image:0"" Name=""sample"">
    <Pixels ID=""Pixels:0"" DimensionOrder=""XYCZT"" Type=""uint8""
            SizeX=""4"" SizeY=""4"" SizeZ=""1"" SizeC=""2"" SizeT=""1""
            PhysicalSizeX=""0.5"" PhysicalSizeY=""0.5"" PhysicalSizeZ=""1.0""
            PhysicalSizeXUnit=""µm"" PhysicalSizeYUnit=""µm"" PhysicalSizeZUnit=""µm"">
      <Channel ID=""Channel:0:0"" Name=""DAPI"" SamplesPerPixel=""1"" Color=""-16776961""/>
      <Channel ID=""Channel:0:1"" Name=""GFP""  SamplesPerPixel=""1"" Color=""65535""/>
    </Pixels>
  </Image>
</OME>";

    private static VipsImage Uniform(int w, int h, byte value, int bands = 1)
        => new VipsImage
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

    private static async Task<byte[]> SaveToBytesAsync(System.Func<PipeWriter, Task> save)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms, new StreamPipeWriterOptions(leaveOpen: true));
        await save(writer);
        return ms.ToArray();
    }

    private static IVipsSource SourceFromBytes(byte[] bytes)
        => new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));

    [Fact]
    public void LooksLikeOmeXml_DistinguishesOmeFromGenericXml()
    {
        Assert.True(VipsOmeTiff.LooksLikeOmeXml(SampleOmeXml));
        Assert.True(VipsOmeTiff.LooksLikeOmeXml("<OME>foo</OME>"));
        Assert.False(VipsOmeTiff.LooksLikeOmeXml("<svg/>"));
        Assert.False(VipsOmeTiff.LooksLikeOmeXml("a comment, not xml at all"));
        Assert.False(VipsOmeTiff.LooksLikeOmeXml(""));
        Assert.False(VipsOmeTiff.LooksLikeOmeXml(null!));
    }

    [Fact]
    public async Task RoundTrip_OmeXmlInImageDescription_SurvivesTiffSaveLoad()
    {
        var src = Uniform(4, 4, value: 100);
        src.Metadata["ome:xml"] = SampleOmeXml;

        var bytes = await SaveToBytesAsync(w => VipsTiffSaver.SaveAsync(src, w));
        var loaded = await VipsTiffLoader.LoadAsync(SourceFromBytes(bytes));

        Assert.NotNull(loaded);
        Assert.True(VipsOmeTiff.IsOmeTiff(loaded!));
        var ome = VipsOmeTiff.GetOmeXml(loaded);
        Assert.NotNull(ome);
        Assert.Contains("PhysicalSizeX=\"0.5\"", ome);
        Assert.Contains("Channel:0:0", ome);
    }

    [Fact]
    public async Task LoadingOmeTiff_SetsXResYResFromPhysicalSize()
    {
        // PhysicalSizeX = 0.5 µm/pixel → 1 / (0.5e-3 mm) = 2000 px/mm.
        var src = Uniform(4, 4, value: 100);
        src.Metadata["ome:xml"] = SampleOmeXml;
        var bytes = await SaveToBytesAsync(w => VipsTiffSaver.SaveAsync(src, w));

        var loaded = await VipsTiffLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        Assert.Equal(2000.0, loaded!.XRes, 1.0);
        Assert.Equal(2000.0, loaded.YRes, 1.0);
    }

    [Fact]
    public async Task GetOmePhysicalSize_ReturnsParsedValues()
    {
        var src = Uniform(4, 4, value: 100);
        src.Metadata["ome:xml"] = SampleOmeXml;
        var bytes = await SaveToBytesAsync(w => VipsTiffSaver.SaveAsync(src, w));

        var loaded = await VipsTiffLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        var sz = VipsOmeTiff.GetOmePhysicalSize(loaded!);
        Assert.NotNull(sz);
        Assert.Equal(0.5, sz!.X);
        Assert.Equal(0.5, sz.Y);
        Assert.Equal(1.0, sz.Z);
        Assert.Equal("µm", sz.Unit);
    }

    [Fact]
    public async Task GetOmeChannels_ReturnsParsedChannels()
    {
        var src = Uniform(4, 4, value: 100);
        src.Metadata["ome:xml"] = SampleOmeXml;
        var bytes = await SaveToBytesAsync(w => VipsTiffSaver.SaveAsync(src, w));

        var loaded = await VipsTiffLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        var channels = VipsOmeTiff.GetOmeChannels(loaded!);
        Assert.NotNull(channels);
        Assert.Equal(2, channels!.Length);
        Assert.Equal("DAPI", channels[0].Name);
        Assert.Equal("GFP", channels[1].Name);
        Assert.Equal(1, channels[0].SamplesPerPixel);
    }

    [Fact]
    public async Task GenericTiffImageDescription_RoundTripsWithoutOmeKey()
    {
        var src = Uniform(4, 4, value: 100);
        src.Metadata["tiff:image-description"] = "ordinary photographer's notes, not OME-XML";
        var bytes = await SaveToBytesAsync(w => VipsTiffSaver.SaveAsync(src, w));

        var loaded = await VipsTiffLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        Assert.Equal("ordinary photographer's notes, not OME-XML",
            loaded!.Metadata["tiff:image-description"]);
        Assert.False(VipsOmeTiff.IsOmeTiff(loaded));
    }

    [Fact]
    public void GetOmePhysicalSize_AbsentXml_ReturnsNull()
    {
        var img = Uniform(2, 2, 0);
        Assert.Null(VipsOmeTiff.GetOmePhysicalSize(img));
        Assert.Null(VipsOmeTiff.GetOmeChannels(img));
        Assert.False(VipsOmeTiff.IsOmeTiff(img));
    }

    [Fact]
    public void PopulatePhysicalSize_HandlesUnits()
    {
        var img = Uniform(2, 2, 0);
        img.Metadata["ome:xml"] = "<OME><Image><Pixels PhysicalSizeX=\"2\" PhysicalSizeY=\"2\" PhysicalSizeXUnit=\"mm\"/></Image></OME>";
        VipsOmeTiff.PopulatePhysicalSize(img);
        // 2 mm/pixel → 0.5 px/mm
        Assert.Equal(0.5, img.XRes, 1e-6);
    }

    [Fact]
    public void PopulatePhysicalSize_NmUnit()
    {
        var img = Uniform(2, 2, 0);
        img.Metadata["ome:xml"] = "<OME><Image><Pixels PhysicalSizeX=\"100\" PhysicalSizeY=\"100\" PhysicalSizeXUnit=\"nm\"/></Image></OME>";
        VipsOmeTiff.PopulatePhysicalSize(img);
        // 100 nm/pixel = 0.0001 mm/pixel → 10000 px/mm
        Assert.Equal(10000.0, img.XRes, 1e-3);
    }
}
