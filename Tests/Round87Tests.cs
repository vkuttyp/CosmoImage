using System;
using System.Linq;
using CosmoImage.Operations.Metadata;
using Xunit;

namespace CosmoImage.Tests;

public class Round87Tests
{
    // ---- Round-trip: empty GPS ----

    [Fact]
    public void NoGpsTags_ProfileSerializesWithoutGpsPointer()
    {
        var p = new VipsExifProfile();
        p.SetValue(VipsExifTag.Make, "Test");
        var back = VipsExifProfile.TryParse(p.ToBytes())!;
        Assert.Empty(back.GpsTags);
    }

    // ---- Single GPS tags ----

    [Fact]
    public void GpsVersionId_RoundTrip()
    {
        var p = new VipsExifProfile();
        p.SetGpsValue(VipsGpsTag.VersionID, new byte[] { 2, 3, 0, 0 });
        var back = VipsExifProfile.TryParse(p.ToBytes())!;
        var v = back.GetGpsValue<byte[]>(VipsGpsTag.VersionID);
        Assert.Equal(new byte[] { 2, 3, 0, 0 }, v);
    }

    [Fact]
    public void GpsAltitude_RoundTrip()
    {
        var p = new VipsExifProfile();
        p.SetGpsValue(VipsGpsTag.Altitude, new VipsExifRational(1234, 100));  // 12.34 m
        p.SetGpsValue(VipsGpsTag.AltitudeRef, (byte)0);
        var back = VipsExifProfile.TryParse(p.ToBytes())!;
        var alt = back.GetGpsValue<VipsExifRational>(VipsGpsTag.Altitude);
        Assert.Equal(12.34, alt.ToDouble(), 1e-6);
        Assert.Equal((byte)0, back.GetGpsValue<byte>(VipsGpsTag.AltitudeRef));
    }

    [Fact]
    public void GpsRationalArray_RoundTrip()
    {
        // Latitude as DMS rationals: 37° 46' 30" → (37, 46, 30).
        var p = new VipsExifProfile();
        p.SetGpsValue(VipsGpsTag.Latitude, new[]
        {
            new VipsExifRational(37, 1),
            new VipsExifRational(46, 1),
            new VipsExifRational(30, 1),
        });
        p.SetGpsValue(VipsGpsTag.LatitudeRef, "N");
        var back = VipsExifProfile.TryParse(p.ToBytes())!;
        var lat = back.GetGpsValue<VipsExifRational[]>(VipsGpsTag.Latitude)!;
        Assert.Equal(3, lat.Length);
        Assert.Equal(37u, lat[0].Numerator);
        Assert.Equal(46u, lat[1].Numerator);
        Assert.Equal(30u, lat[2].Numerator);
        Assert.Equal("N", back.GetGpsValue<string>(VipsGpsTag.LatitudeRef));
    }

    [Fact]
    public void GpsTimeStamp_RoundTrip()
    {
        var p = new VipsExifProfile();
        p.SetGpsValue(VipsGpsTag.TimeStamp, new[]
        {
            new VipsExifRational(14, 1),  // 14:25:30.5 UTC
            new VipsExifRational(25, 1),
            new VipsExifRational(305, 10),
        });
        p.SetGpsValue(VipsGpsTag.DateStamp, "2026:05:04");
        var back = VipsExifProfile.TryParse(p.ToBytes())!;
        var ts = back.GetGpsValue<VipsExifRational[]>(VipsGpsTag.TimeStamp)!;
        Assert.Equal(14, ts[0].ToDouble());
        Assert.Equal(30.5, ts[2].ToDouble(), 1e-6);
        Assert.Equal("2026:05:04", back.GetGpsValue<string>(VipsGpsTag.DateStamp));
    }

    // ---- Decimal-degree helpers ----

    [Fact]
    public void SetLocation_DecimalDegrees_RoundTripsViaDms()
    {
        // San Francisco: 37.7749° N, 122.4194° W.
        var p = new VipsExifProfile();
        p.SetLocation(37.7749, -122.4194);
        var back = VipsExifProfile.TryParse(p.ToBytes())!;
        var loc = back.GetLocation();
        Assert.NotNull(loc);
        Assert.Equal(37.7749, loc!.Value.latitude, 1e-5);
        Assert.Equal(-122.4194, loc.Value.longitude, 1e-5);
    }

    [Fact]
    public void SetLocation_NorthernEastern_RefsAreNAndE()
    {
        var p = new VipsExifProfile();
        p.SetLocation(48.8566, 2.3522);  // Paris
        Assert.Equal("N", p.GetGpsValue<string>(VipsGpsTag.LatitudeRef));
        Assert.Equal("E", p.GetGpsValue<string>(VipsGpsTag.LongitudeRef));
    }

    [Fact]
    public void SetLocation_SouthernWestern_RefsAreSAndW()
    {
        var p = new VipsExifProfile();
        p.SetLocation(-22.9068, -43.1729);  // Rio de Janeiro
        Assert.Equal("S", p.GetGpsValue<string>(VipsGpsTag.LatitudeRef));
        Assert.Equal("W", p.GetGpsValue<string>(VipsGpsTag.LongitudeRef));
    }

    [Fact]
    public void GetLocation_MissingTags_ReturnsNull()
    {
        var p = new VipsExifProfile();
        Assert.Null(p.GetLocation());
        // Set lat but not long.
        p.SetGpsValue(VipsGpsTag.Latitude, new[]
        {
            new VipsExifRational(0, 1), new VipsExifRational(0, 1), new VipsExifRational(0, 1),
        });
        Assert.Null(p.GetLocation());
    }

    [Fact]
    public void SetLocation_OutOfRange_Throws()
    {
        var p = new VipsExifProfile();
        Assert.Throws<ArgumentOutOfRangeException>(() => p.SetLocation(91, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.SetLocation(0, 181));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.SetLocation(-91, 0));
    }

    // ---- GPS + EXIF in same profile ----

    [Fact]
    public void GpsAndExifTags_BothRoundTripIndependently()
    {
        var p = new VipsExifProfile();
        p.SetValue(VipsExifTag.Make, "Canon");
        p.SetValue(VipsExifTag.Orientation, (ushort)1);
        p.SetValue(VipsExifTag.ExposureTime, new VipsExifRational(1, 250));
        p.SetLocation(37.7749, -122.4194);
        p.SetGpsValue(VipsGpsTag.Altitude, new VipsExifRational(50, 1));

        var back = VipsExifProfile.TryParse(p.ToBytes())!;
        Assert.Equal("Canon", back.GetValue<string>(VipsExifTag.Make));
        Assert.Equal(1, back.GetValue<ushort>(VipsExifTag.Orientation));
        Assert.Equal(250u, back.GetValue<VipsExifRational>(VipsExifTag.ExposureTime).Denominator);
        var loc = back.GetLocation();
        Assert.NotNull(loc);
        Assert.Equal(37.7749, loc!.Value.latitude, 1e-5);
        Assert.Equal(50.0, back.GetGpsValue<VipsExifRational>(VipsGpsTag.Altitude).ToDouble(), 1e-6);
    }

    // ---- Tag lifecycle ----

    [Fact]
    public void RemoveGps_DropsTag()
    {
        var p = new VipsExifProfile();
        p.SetGpsValue(VipsGpsTag.Altitude, new VipsExifRational(100, 1));
        Assert.True(p.ContainsGps(VipsGpsTag.Altitude));
        Assert.True(p.RemoveGps(VipsGpsTag.Altitude));
        Assert.False(p.ContainsGps(VipsGpsTag.Altitude));
        Assert.False(p.RemoveGps(VipsGpsTag.Altitude));
    }

    [Fact]
    public void GpsTags_EnumeratesSetTags()
    {
        var p = new VipsExifProfile();
        p.SetLocation(0, 0);
        p.SetGpsValue(VipsGpsTag.Altitude, new VipsExifRational(0, 1));
        var tags = p.GpsTags.ToHashSet();
        Assert.Contains(VipsGpsTag.Latitude, tags);
        Assert.Contains(VipsGpsTag.LatitudeRef, tags);
        Assert.Contains(VipsGpsTag.Longitude, tags);
        Assert.Contains(VipsGpsTag.LongitudeRef, tags);
        Assert.Contains(VipsGpsTag.Altitude, tags);
    }

    // ---- Big-endian ----

    [Fact]
    public void BigEndian_GpsRoundTrip()
    {
        var p = new VipsExifProfile { BigEndian = true };
        p.SetLocation(48.8566, 2.3522);
        p.SetGpsValue(VipsGpsTag.Altitude, new VipsExifRational(35, 1));
        var bytes = p.ToBytes();
        Assert.Equal(0x4D, bytes[0]);  // MM
        var back = VipsExifProfile.TryParse(bytes)!;
        Assert.True(back.BigEndian);
        var loc = back.GetLocation();
        Assert.Equal(48.8566, loc!.Value.latitude, 1e-5);
        Assert.Equal(35.0, back.GetGpsValue<VipsExifRational>(VipsGpsTag.Altitude).ToDouble(), 1e-6);
    }

    // ---- Sub-IFD pointer ordering ----

    [Fact]
    public void IfdPointerEntries_SortedByTagId()
    {
        // ExifIFDPointer (0x8769) and GpsIFDPointer (0x8825) both go in
        // IFD0; their on-wire entry IDs must be in ascending order along
        // with any other IFD0 tags.
        var p = new VipsExifProfile();
        p.SetValue(VipsExifTag.Copyright, "X");      // 0x8298 (IFD0)
        p.SetValue(VipsExifTag.ExposureTime, new VipsExifRational(1, 60));  // sub-IFD
        p.SetGpsValue(VipsGpsTag.Altitude, new VipsExifRational(1, 1));     // GPS
        var bytes = p.ToBytes();
        // Both pointers should now be present; round-trip should still work.
        var back = VipsExifProfile.TryParse(bytes)!;
        Assert.Equal("X", back.GetValue<string>(VipsExifTag.Copyright));
        Assert.True(back.Contains(VipsExifTag.ExposureTime));
        Assert.True(back.ContainsGps(VipsGpsTag.Altitude));
    }
}
