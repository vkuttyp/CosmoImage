using System;
using System.Linq;
using CosmoImage.Operations.Metadata;
using Xunit;

namespace CosmoImage.Tests;

public class Round83Tests
{
    // ---- Round-trip integrity ----

    [Fact]
    public void Empty_RoundTrip_ProducesValidBlob()
    {
        var p = new VipsExifProfile();
        var bytes = p.ToBytes();
        var back = VipsExifProfile.TryParse(bytes);
        Assert.NotNull(back);
        Assert.Empty(back!.Tags);
    }

    [Fact]
    public void Orientation_RoundTrip()
    {
        var p = new VipsExifProfile();
        p.SetValue(VipsExifTag.Orientation, (ushort)6);
        var back = VipsExifProfile.TryParse(p.ToBytes())!;
        Assert.Equal(6, back.GetValue<ushort>(VipsExifTag.Orientation));
    }

    [Fact]
    public void StringTag_RoundTrip()
    {
        var p = new VipsExifProfile();
        p.SetValue(VipsExifTag.Make, "Canon");
        p.SetValue(VipsExifTag.Model, "EOS R5");
        var back = VipsExifProfile.TryParse(p.ToBytes())!;
        Assert.Equal("Canon", back.GetValue<string>(VipsExifTag.Make));
        Assert.Equal("EOS R5", back.GetValue<string>(VipsExifTag.Model));
    }

    [Fact]
    public void RationalTag_RoundTrip()
    {
        var p = new VipsExifProfile();
        p.SetValue(VipsExifTag.ExposureTime, new VipsExifRational(1, 250));
        p.SetValue(VipsExifTag.FNumber, new VipsExifRational(28, 10));   // f/2.8
        var back = VipsExifProfile.TryParse(p.ToBytes())!;
        var et = back.GetValue<VipsExifRational>(VipsExifTag.ExposureTime);
        Assert.Equal(1u, et.Numerator);
        Assert.Equal(250u, et.Denominator);
        Assert.Equal(0.004, et.ToDouble(), 1e-9);
        Assert.Equal(2.8, back.GetValue<VipsExifRational>(VipsExifTag.FNumber).ToDouble(), 1e-9);
    }

    [Fact]
    public void LongTag_RoundTrip()
    {
        var p = new VipsExifProfile();
        p.SetValue(VipsExifTag.PixelXDimension, 1920u);
        p.SetValue(VipsExifTag.PixelYDimension, 1080u);
        var back = VipsExifProfile.TryParse(p.ToBytes())!;
        Assert.Equal(1920u, back.GetValue<uint>(VipsExifTag.PixelXDimension));
        Assert.Equal(1080u, back.GetValue<uint>(VipsExifTag.PixelYDimension));
    }

    // ---- Sub-IFD ----

    [Fact]
    public void SubIfdTag_RoundTripsViaExifIfdPointer()
    {
        // ExposureTime lives in the Exif sub-IFD; it should round-trip
        // via the auto-emitted ExifIFDPointer from IFD0.
        var p = new VipsExifProfile();
        p.SetValue(VipsExifTag.ExposureTime, new VipsExifRational(1, 60));
        p.SetValue(VipsExifTag.ISOSpeedRatings, (ushort)400);
        var bytes = p.ToBytes();
        var back = VipsExifProfile.TryParse(bytes)!;
        Assert.True(back.Contains(VipsExifTag.ExposureTime));
        Assert.True(back.Contains(VipsExifTag.ISOSpeedRatings));
        Assert.Equal(400, back.GetValue<ushort>(VipsExifTag.ISOSpeedRatings));
    }

    [Fact]
    public void MixedIfd0AndSubIfd_RoundTrip()
    {
        var p = new VipsExifProfile();
        p.SetValue(VipsExifTag.Make, "Canon");                              // IFD0
        p.SetValue(VipsExifTag.Orientation, (ushort)1);                     // IFD0
        p.SetValue(VipsExifTag.ISOSpeedRatings, (ushort)100);               // sub-IFD
        p.SetValue(VipsExifTag.LensModel, "RF 50mm F1.2 L USM");            // sub-IFD
        var back = VipsExifProfile.TryParse(p.ToBytes())!;
        Assert.Equal("Canon", back.GetValue<string>(VipsExifTag.Make));
        Assert.Equal(1, back.GetValue<ushort>(VipsExifTag.Orientation));
        Assert.Equal(100, back.GetValue<ushort>(VipsExifTag.ISOSpeedRatings));
        Assert.Equal("RF 50mm F1.2 L USM", back.GetValue<string>(VipsExifTag.LensModel));
    }

    // ---- Endianness ----

    [Fact]
    public void BigEndian_RoundTrip()
    {
        var p = new VipsExifProfile { BigEndian = true };
        p.SetValue(VipsExifTag.Orientation, (ushort)6);
        p.SetValue(VipsExifTag.Make, "Test");
        p.SetValue(VipsExifTag.ExposureTime, new VipsExifRational(1, 125));
        var bytes = p.ToBytes();
        // Header should be MM (big-endian).
        Assert.Equal(0x4D, bytes[0]);
        Assert.Equal(0x4D, bytes[1]);
        var back = VipsExifProfile.TryParse(bytes)!;
        Assert.True(back.BigEndian);
        Assert.Equal(6, back.GetValue<ushort>(VipsExifTag.Orientation));
        Assert.Equal("Test", back.GetValue<string>(VipsExifTag.Make));
        Assert.Equal(125u, back.GetValue<VipsExifRational>(VipsExifTag.ExposureTime).Denominator);
    }

    [Fact]
    public void ParsedProfile_PreservesByteOrder()
    {
        var p = new VipsExifProfile { BigEndian = true };
        p.SetValue(VipsExifTag.Make, "Canon");
        var bytes = p.ToBytes();
        var back = VipsExifProfile.TryParse(bytes)!;
        Assert.True(back.BigEndian);
        // Re-serialise: same byte order on output.
        var bytes2 = back.ToBytes();
        Assert.Equal(0x4D, bytes2[0]);
    }

    // ---- Tag lifecycle ----

    [Fact]
    public void Tags_EnumeratesSetTags()
    {
        var p = new VipsExifProfile();
        p.SetValue(VipsExifTag.Make, "X");
        p.SetValue(VipsExifTag.Orientation, (ushort)1);
        var tags = p.Tags.ToHashSet();
        Assert.Contains(VipsExifTag.Make, tags);
        Assert.Contains(VipsExifTag.Orientation, tags);
        Assert.Equal(2, tags.Count);
    }

    [Fact]
    public void Remove_DropsTag()
    {
        var p = new VipsExifProfile();
        p.SetValue(VipsExifTag.Orientation, (ushort)6);
        Assert.True(p.Contains(VipsExifTag.Orientation));
        Assert.True(p.Remove(VipsExifTag.Orientation));
        Assert.False(p.Contains(VipsExifTag.Orientation));
        Assert.False(p.Remove(VipsExifTag.Orientation));  // already gone
    }

    [Fact]
    public void GetValue_MissingTag_ReturnsDefault()
    {
        var p = new VipsExifProfile();
        Assert.Null(p.GetValue<string>(VipsExifTag.Make));
        Assert.Equal(0, p.GetValue<ushort>(VipsExifTag.Orientation));
    }

    // ---- Malformed input ----

    [Fact]
    public void TryParse_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(VipsExifProfile.TryParse(null));
        Assert.Null(VipsExifProfile.TryParse(Array.Empty<byte>()));
    }

    [Fact]
    public void TryParse_BadHeader_ReturnsNull()
    {
        // Random bytes — no II/MM start marker.
        Assert.Null(VipsExifProfile.TryParse(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 8 }));
    }

    [Fact]
    public void TryParse_BadMagic_ReturnsNull()
    {
        // Valid II marker but wrong magic.
        Assert.Null(VipsExifProfile.TryParse(new byte[] { 0x49, 0x49, 0xFF, 0xFF, 0, 0, 0, 8 }));
    }

    // ---- VipsImage extension methods ----

    [Fact]
    public void VipsImage_RoundTripExifProfileBlob()
    {
        // Build, serialize, attach to image, retrieve.
        var profile = new VipsExifProfile();
        profile.SetValue(VipsExifTag.Orientation, (ushort)6);
        profile.SetValue(VipsExifTag.Make, "TestMaker");
        profile.SetValue(VipsExifTag.ExposureTime, new VipsExifRational(1, 100));

        var image = new VipsImage { Width = 10, Height = 10, Bands = 3 };
        image.SetExifProfile(profile);

        var retrieved = image.GetExifProfile();
        Assert.NotNull(retrieved);
        Assert.Equal(6, retrieved!.GetValue<ushort>(VipsExifTag.Orientation));
        Assert.Equal("TestMaker", retrieved.GetValue<string>(VipsExifTag.Make));
        Assert.Equal(100u, retrieved.GetValue<VipsExifRational>(VipsExifTag.ExposureTime).Denominator);
    }

    [Fact]
    public void VipsImage_NoExifBlob_GetExifProfileReturnsNull()
    {
        var image = new VipsImage { Width = 10, Height = 10 };
        Assert.Null(image.GetExifProfile());
    }

    // ---- Sort order on serialization ----

    [Fact]
    public void Serialization_OrdersEntriesByTagId()
    {
        // EXIF spec requires entries sorted by tag ID. Insert in
        // reverse and confirm bytes contain entries in ascending order.
        var p = new VipsExifProfile();
        p.SetValue(VipsExifTag.Software, "A");          // 0x0131
        p.SetValue(VipsExifTag.Make, "B");              // 0x010F
        p.SetValue(VipsExifTag.Model, "C");             // 0x0110

        var bytes = p.ToBytes();
        // First entry tag ID at offset 8 + 2 (header + entry count).
        ushort firstTag = (ushort)(bytes[10] | (bytes[11] << 8));  // little-endian default
        Assert.Equal(0x010F, firstTag);  // Make should sort first
    }
}
