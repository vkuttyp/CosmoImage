using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 195 — OME-TIFF Z/C/T full N-D mapping. Extends
/// <see cref="VipsOmeTiff"/> to expose the full layout
/// (SizeX/Y/Z/C/T + DimensionOrder + Type) parsed from OME-XML, and
/// surfaces the axis counts as <c>ome:size-z / ome:size-c /
/// ome:size-t / ome:dimension-order</c> metadata when the TIFF loader
/// detects OME-XML.
///
/// <para>Same height-stacking convention as NIfTI 4D (round 193) and
/// FITS NAXIS≥4 (round 194): the multi-page TIFF buffer
/// (<c>n-pages</c> · <c>page-height</c>) is the underlying storage;
/// the new metadata tells consumers how those pages decompose into
/// the (Z, C, T) grid.</para>
/// </summary>
public class Round195Tests
{
    private const string Ome3DOmeXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<OME xmlns=""http://www.openmicroscopy.org/Schemas/OME/2016-06"">
  <Image ID=""Image:0"" Name=""sample"">
    <Pixels ID=""Pixels:0"" DimensionOrder=""XYZCT"" Type=""uint16""
            SizeX=""512"" SizeY=""512"" SizeZ=""20"" SizeC=""3"" SizeT=""5"">
      <Channel ID=""Channel:0:0"" Name=""DAPI""/>
      <Channel ID=""Channel:0:1"" Name=""GFP""/>
      <Channel ID=""Channel:0:2"" Name=""DsRed""/>
    </Pixels>
  </Image>
</OME>";

    private const string OmeSinglePlaneXml = @"<?xml version=""1.0""?>
<OME xmlns=""http://www.openmicroscopy.org/Schemas/OME/2016-06"">
  <Image ID=""Image:0"">
    <Pixels ID=""Pixels:0"" DimensionOrder=""XYCZT"" Type=""uint8""
            SizeX=""4"" SizeY=""4"" SizeZ=""1"" SizeC=""1"" SizeT=""1"" />
  </Image>
</OME>";

    private static VipsImage MakeImage(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => 0,
        };

    [Fact]
    public void GetOmePixelsLayout_ParsesAllAttributes()
    {
        var img = MakeImage(512, 512);
        img.Metadata["ome:xml"] = Ome3DOmeXml;

        var layout = VipsOmeTiff.GetOmePixelsLayout(img);
        Assert.NotNull(layout);
        Assert.Equal(512, layout!.SizeX);
        Assert.Equal(512, layout.SizeY);
        Assert.Equal(20, layout.SizeZ);
        Assert.Equal(3, layout.SizeC);
        Assert.Equal(5, layout.SizeT);
        Assert.Equal("XYZCT", layout.DimensionOrder);
        Assert.Equal("uint16", layout.Type);
    }

    [Fact]
    public void GetOmePixelsLayout_ReturnsNull_WhenNoOmeXml()
    {
        var img = MakeImage(4, 4);
        Assert.Null(VipsOmeTiff.GetOmePixelsLayout(img));
    }

    [Fact]
    public void PopulatePixelsLayout_AddsAxisMetadata_When_NDimGreaterThan1()
    {
        var img = MakeImage(512, 512);
        img.Metadata["ome:xml"] = Ome3DOmeXml;
        VipsOmeTiff.PopulatePixelsLayout(img);

        Assert.Equal("20", img.Metadata["ome:size-z"]);
        Assert.Equal("3", img.Metadata["ome:size-c"]);
        Assert.Equal("5", img.Metadata["ome:size-t"]);
        Assert.Equal("XYZCT", img.Metadata["ome:dimension-order"]);
    }

    [Fact]
    public void PopulatePixelsLayout_SkipsTrivialAxes()
    {
        // SingleZ/C/T = 1 → no metadata key for those axes.
        // The XYCZT dimension-order still gets surfaced (a non-empty
        // string is meaningful even at 2D).
        var img = MakeImage(4, 4);
        img.Metadata["ome:xml"] = OmeSinglePlaneXml;
        VipsOmeTiff.PopulatePixelsLayout(img);

        Assert.False(img.Metadata.ContainsKey("ome:size-z"));
        Assert.False(img.Metadata.ContainsKey("ome:size-c"));
        Assert.False(img.Metadata.ContainsKey("ome:size-t"));
        Assert.Equal("XYCZT", img.Metadata["ome:dimension-order"]);
    }

    [Fact]
    public void PopulatePixelsLayout_NoOp_WhenNoOmeXml()
    {
        var img = MakeImage(4, 4);
        VipsOmeTiff.PopulatePixelsLayout(img);

        Assert.False(img.Metadata.ContainsKey("ome:size-z"));
        Assert.False(img.Metadata.ContainsKey("ome:dimension-order"));
    }

    [Fact]
    public async Task TiffRoundTrip_OmeXml_PopulatesAxisMetadataOnLoad()
    {
        // Save a synthetic image with the OME XML in image-description,
        // load it back, and verify the loader auto-populated the new
        // axis-count metadata.
        var src = MakeImage(4, 4);
        src.Metadata["ome:xml"] = Ome3DOmeXml;

        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms, new StreamPipeWriterOptions(leaveOpen: true));
        await VipsTiffSaver.SaveAsync(src, writer);

        var loadSrc = new PipeVipsSource(PipeReader.Create(new MemoryStream(ms.ToArray())));
        var loaded = await VipsTiffLoader.LoadAsync(loadSrc);
        Assert.NotNull(loaded);

        Assert.Equal("20", loaded!.Metadata["ome:size-z"]);
        Assert.Equal("3", loaded.Metadata["ome:size-c"]);
        Assert.Equal("5", loaded.Metadata["ome:size-t"]);
        Assert.Equal("XYZCT", loaded.Metadata["ome:dimension-order"]);
    }
}
