using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CosmoImage.Operations.Metadata;
using Xunit;

namespace CosmoImage.Tests;

public class Round84Tests
{
    // ---- Round-trip integrity ----

    [Fact]
    public void Empty_RoundTrip_ProducesEmptyBlob()
    {
        var p = new VipsIptcProfile();
        var bytes = p.ToBytes();
        Assert.Empty(bytes);
        var back = VipsIptcProfile.TryParse(bytes);
        // Empty input → null (we treat zero-length as "no profile").
        Assert.Null(back);
    }

    [Fact]
    public void SingleStringTag_RoundTrip()
    {
        var p = new VipsIptcProfile();
        p.SetValue(VipsIptcTag.ObjectName, "Sunrise on the Bay");
        var back = VipsIptcProfile.TryParse(p.ToBytes())!;
        Assert.Equal("Sunrise on the Bay", back.GetValue(VipsIptcTag.ObjectName));
    }

    [Fact]
    public void MultipleSingleTags_RoundTrip()
    {
        var p = new VipsIptcProfile();
        p.SetValue(VipsIptcTag.City, "San Francisco");
        p.SetValue(VipsIptcTag.ProvinceState, "California");
        p.SetValue(VipsIptcTag.CountryPrimaryLocationCode, "USA");
        p.SetValue(VipsIptcTag.CountryPrimaryLocationName, "United States of America");
        var back = VipsIptcProfile.TryParse(p.ToBytes())!;
        Assert.Equal("San Francisco", back.GetValue(VipsIptcTag.City));
        Assert.Equal("California", back.GetValue(VipsIptcTag.ProvinceState));
        Assert.Equal("USA", back.GetValue(VipsIptcTag.CountryPrimaryLocationCode));
        Assert.Equal("United States of America", back.GetValue(VipsIptcTag.CountryPrimaryLocationName));
    }

    // ---- Repeatable tags ----

    [Fact]
    public void Keywords_Repeatable_RoundTripsAllValues()
    {
        var p = new VipsIptcProfile();
        p.Add(VipsIptcTag.Keywords, "landscape");
        p.Add(VipsIptcTag.Keywords, "sunset");
        p.Add(VipsIptcTag.Keywords, "ocean");
        var back = VipsIptcProfile.TryParse(p.ToBytes())!;
        var keywords = back.GetValues(VipsIptcTag.Keywords);
        Assert.Equal(3, keywords.Count);
        Assert.Contains("landscape", keywords);
        Assert.Contains("sunset", keywords);
        Assert.Contains("ocean", keywords);
    }

    [Fact]
    public void SetValues_ReplacesAllPreviousValues()
    {
        var p = new VipsIptcProfile();
        p.Add(VipsIptcTag.Byline, "Old name");
        p.SetValues(VipsIptcTag.Byline, new[] { "Alice", "Bob" });
        var back = VipsIptcProfile.TryParse(p.ToBytes())!;
        var bylines = back.GetValues(VipsIptcTag.Byline);
        Assert.Equal(2, bylines.Count);
        Assert.Equal("Alice", bylines[0]);
        Assert.Equal("Bob", bylines[1]);
    }

    [Fact]
    public void SetValue_ReplacesPreviousValue()
    {
        var p = new VipsIptcProfile();
        p.Add(VipsIptcTag.Caption, "First caption");
        p.SetValue(VipsIptcTag.Caption, "Replacement caption");
        Assert.Single(p.GetValues(VipsIptcTag.Caption));
        Assert.Equal("Replacement caption", p.GetValue(VipsIptcTag.Caption));
    }

    // ---- Unicode ----

    [Fact]
    public void UnicodeStrings_RoundTripViaUtf8()
    {
        var p = new VipsIptcProfile();
        p.SetValue(VipsIptcTag.City, "São Paulo");
        p.SetValue(VipsIptcTag.Caption, "日本語キャプション");
        p.Add(VipsIptcTag.Keywords, "naturaleza");
        var back = VipsIptcProfile.TryParse(p.ToBytes())!;
        Assert.Equal("São Paulo", back.GetValue(VipsIptcTag.City));
        Assert.Equal("日本語キャプション", back.GetValue(VipsIptcTag.Caption));
        Assert.Equal("naturaleza", back.GetValue(VipsIptcTag.Keywords));
    }

    // ---- Long values ----

    [Fact]
    public void LongCaption_RoundTrip()
    {
        // Long but under 32K — uses standard 2-byte length.
        var caption = new string('x', 1500);
        var p = new VipsIptcProfile();
        p.SetValue(VipsIptcTag.Caption, caption);
        var back = VipsIptcProfile.TryParse(p.ToBytes())!;
        Assert.Equal(caption, back.GetValue(VipsIptcTag.Caption));
    }

    [Fact]
    public void ExtendedLength_Over32K_RoundTrips()
    {
        // Force the extended-length path.
        var huge = new string('y', 50_000);
        var p = new VipsIptcProfile();
        p.SetValue(VipsIptcTag.Caption, huge);
        var bytes = p.ToBytes();
        // Extended marker: high bit of length-MSB set.
        // After tag-marker, record, dataset (3 bytes), the 4th byte is 0x80.
        Assert.Equal(0x80, bytes[3]);
        var back = VipsIptcProfile.TryParse(bytes)!;
        Assert.Equal(huge, back.GetValue(VipsIptcTag.Caption));
    }

    // ---- Tag lifecycle ----

    [Fact]
    public void Tags_EnumeratesSetTags()
    {
        var p = new VipsIptcProfile();
        p.SetValue(VipsIptcTag.Headline, "X");
        p.SetValue(VipsIptcTag.Source, "Y");
        var tags = p.Tags.ToHashSet();
        Assert.Contains(VipsIptcTag.Headline, tags);
        Assert.Contains(VipsIptcTag.Source, tags);
        Assert.Equal(2, tags.Count);
    }

    [Fact]
    public void Remove_DropsAllValuesForTag()
    {
        var p = new VipsIptcProfile();
        p.Add(VipsIptcTag.Keywords, "a");
        p.Add(VipsIptcTag.Keywords, "b");
        Assert.True(p.Remove(VipsIptcTag.Keywords));
        Assert.False(p.Contains(VipsIptcTag.Keywords));
        Assert.Empty(p.GetValues(VipsIptcTag.Keywords));
    }

    [Fact]
    public void GetValue_MissingTag_ReturnsNull()
    {
        var p = new VipsIptcProfile();
        Assert.Null(p.GetValue(VipsIptcTag.Caption));
        Assert.Empty(p.GetValues(VipsIptcTag.Keywords));
    }

    // ---- Malformed input ----

    [Fact]
    public void TryParse_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(VipsIptcProfile.TryParse(null));
        Assert.Null(VipsIptcProfile.TryParse(Array.Empty<byte>()));
    }

    [Fact]
    public void TryParse_BadMarker_ReturnsNull()
    {
        // Should start with 0x1C; provide 0xFF instead.
        Assert.Null(VipsIptcProfile.TryParse(new byte[] { 0xFF, 2, 5, 0, 1, (byte)'X' }));
    }

    [Fact]
    public void TryParse_TruncatedValue_ReturnsNull()
    {
        // Length says 100 but we only have a few value bytes.
        var bytes = new byte[] { 0x1C, 2, 5, 0, 100, (byte)'X' };
        Assert.Null(VipsIptcProfile.TryParse(bytes));
    }

    // ---- Sort order on serialization ----

    [Fact]
    public void Serialization_OrdersByDatasetId()
    {
        // Insert tags out of dataset-ID order; verify on-wire order is ascending.
        var p = new VipsIptcProfile();
        p.SetValue(VipsIptcTag.Source, "S");           // 115
        p.SetValue(VipsIptcTag.City, "C");             // 90
        p.SetValue(VipsIptcTag.ObjectName, "T");       // 5
        var bytes = p.ToBytes();
        // Read out the dataset bytes in order: each entry is (0x1C, 2, dataset, ...).
        var datasets = new List<int>();
        int p_pos = 0;
        while (p_pos < bytes.Length)
        {
            datasets.Add(bytes[p_pos + 2]);
            int len = (bytes[p_pos + 3] << 8) | bytes[p_pos + 4];
            p_pos += 5 + len;
        }
        Assert.Equal(new[] { 5, 90, 115 }, datasets);
    }

    // ---- Non-Application-Record entries skipped ----

    [Fact]
    public void TryParse_IgnoresEntriesFromOtherRecords()
    {
        // Construct: one record-1 entry (envelope) + one record-2 entry.
        // The record-1 entry should be silently skipped.
        var ms = new System.IO.MemoryStream();
        // Record 1, dataset 90 (some envelope tag), value "skip".
        ms.Write(new byte[] { 0x1C, 1, 90, 0, 4 });
        ms.Write(Encoding.UTF8.GetBytes("skip"));
        // Record 2, dataset 5 (ObjectName), value "keep".
        ms.Write(new byte[] { 0x1C, 2, 5, 0, 4 });
        ms.Write(Encoding.UTF8.GetBytes("keep"));
        var profile = VipsIptcProfile.TryParse(ms.ToArray())!;
        Assert.Equal("keep", profile.GetValue(VipsIptcTag.ObjectName));
        Assert.Single(profile.Tags);
    }

    // ---- VipsImage extension methods ----

    [Fact]
    public void VipsImage_RoundTripIptcProfileBlob()
    {
        var profile = new VipsIptcProfile();
        profile.SetValue(VipsIptcTag.City, "Paris");
        profile.Add(VipsIptcTag.Keywords, "architecture");
        profile.Add(VipsIptcTag.Keywords, "europe");

        var image = new VipsImage { Width = 10, Height = 10, Bands = 3 };
        image.SetIptcProfile(profile);

        var retrieved = image.GetIptcProfile();
        Assert.NotNull(retrieved);
        Assert.Equal("Paris", retrieved!.GetValue(VipsIptcTag.City));
        Assert.Equal(2, retrieved.GetValues(VipsIptcTag.Keywords).Count);
    }

    [Fact]
    public void VipsImage_NoIptcBlob_GetIptcProfileReturnsNull()
    {
        var image = new VipsImage { Width = 10, Height = 10 };
        Assert.Null(image.GetIptcProfile());
    }
}
