using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using CosmoImage.Operations.Color;
using CosmoImage.Operations.Metadata;
using Xunit;

namespace CosmoImage.Tests;

public class Round85Tests
{
    /// <summary>
    /// Build a minimal valid ICC profile blob with given header fields
    /// and tags, suitable for round-trip testing.
    /// </summary>
    private static VipsIccProfile MakeMinimalProfile()
    {
        var p = new VipsIccProfile
        {
            ProfileClass = VipsIccProfileClass.DisplayDevice,
            DataColorSpace = VipsIccColorSpace.Rgb,
            ConnectionColorSpace = VipsIccColorSpace.Xyz,
            Version = 0x04300000u,
            CreationDateTime = new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc),
            PreferredCmm = "lcms",
            PrimaryPlatform = "APPL",
            DeviceManufacturer = "TEST",
            DeviceModel = "DUMM",
            ProfileCreator = "ABCD",
            RenderingIntent = VipsIccRenderingIntent.Perceptual,
            PcsIlluminant = new VipsColorXyz(0.9642, 1.0, 0.8249),  // D50
        };
        p.SetTagData("desc", Encoding.ASCII.GetBytes("description blob"));
        p.SetTagData("cprt", Encoding.ASCII.GetBytes("(c) 2026 ACME Corp"));
        return p;
    }

    // ---- Round-trip ----

    [Fact]
    public void RoundTrip_PreservesAllHeaderFields()
    {
        var p = MakeMinimalProfile();
        var bytes = p.ToBytes();
        var back = VipsIccProfile.TryParse(bytes)!;
        Assert.Equal(VipsIccProfileClass.DisplayDevice, back.ProfileClass);
        Assert.Equal(VipsIccColorSpace.Rgb, back.DataColorSpace);
        Assert.Equal(VipsIccColorSpace.Xyz, back.ConnectionColorSpace);
        Assert.Equal(0x04300000u, back.Version);
        Assert.Equal(new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc), back.CreationDateTime);
        Assert.Equal("lcms", back.PreferredCmm);
        Assert.Equal("APPL", back.PrimaryPlatform);
        Assert.Equal("TEST", back.DeviceManufacturer);
        Assert.Equal("DUMM", back.DeviceModel);
        Assert.Equal("ABCD", back.ProfileCreator);
        Assert.Equal(VipsIccRenderingIntent.Perceptual, back.RenderingIntent);
        Assert.Equal(0.9642, back.PcsIlluminant.X, 1e-4);
        Assert.Equal(1.0, back.PcsIlluminant.Y, 1e-4);
        Assert.Equal(0.8249, back.PcsIlluminant.Z, 1e-4);
    }

    [Fact]
    public void RoundTrip_PreservesTagData()
    {
        var p = MakeMinimalProfile();
        var back = VipsIccProfile.TryParse(p.ToBytes())!;
        Assert.True(back.ContainsTag("desc"));
        Assert.True(back.ContainsTag("cprt"));
        Assert.Equal("description blob", Encoding.ASCII.GetString(back.GetTagData("desc")!));
        Assert.Equal("(c) 2026 ACME Corp", Encoding.ASCII.GetString(back.GetTagData("cprt")!));
    }

    [Fact]
    public void RoundTrip_HasMagicAtOffset36()
    {
        var bytes = MakeMinimalProfile().ToBytes();
        var magic = Encoding.ASCII.GetString(bytes, 36, 4);
        Assert.Equal("acsp", magic);
    }

    [Fact]
    public void RoundTrip_ProfileSizeMatchesActualBytes()
    {
        var bytes = MakeMinimalProfile().ToBytes();
        uint headerSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4));
        Assert.Equal((uint)bytes.Length, headerSize);
    }

    // ---- Header-only edits ----

    [Fact]
    public void EditedRenderingIntent_RoundTrips()
    {
        var p = MakeMinimalProfile();
        p.RenderingIntent = VipsIccRenderingIntent.RelativeColorimetric;
        var back = VipsIccProfile.TryParse(p.ToBytes())!;
        Assert.Equal(VipsIccRenderingIntent.RelativeColorimetric, back.RenderingIntent);
    }

    [Fact]
    public void EditedFlagsAndAttributes_RoundTrip()
    {
        var p = MakeMinimalProfile();
        p.Flags = 0x12345678u;
        p.DeviceAttributes = 0xABCDEF0123456789ul;
        var back = VipsIccProfile.TryParse(p.ToBytes())!;
        Assert.Equal(0x12345678u, back.Flags);
        Assert.Equal(0xABCDEF0123456789ul, back.DeviceAttributes);
    }

    // ---- Tag table ----

    [Fact]
    public void TagSignatures_EnumeratesAllTags()
    {
        var p = MakeMinimalProfile();
        p.SetTagData("wtpt", new byte[20]);
        var sigs = VipsIccProfile.TryParse(p.ToBytes())!.TagSignatures.ToHashSet();
        Assert.Contains("desc", sigs);
        Assert.Contains("cprt", sigs);
        Assert.Contains("wtpt", sigs);
    }

    [Fact]
    public void RemoveTag_DropsTag()
    {
        var p = MakeMinimalProfile();
        Assert.True(p.RemoveTag("cprt"));
        Assert.False(p.ContainsTag("cprt"));
        var back = VipsIccProfile.TryParse(p.ToBytes())!;
        Assert.False(back.ContainsTag("cprt"));
        Assert.True(back.ContainsTag("desc"));
    }

    [Fact]
    public void GetTagData_MissingTag_ReturnsNull()
    {
        var p = new VipsIccProfile();
        Assert.Null(p.GetTagData("desc"));
    }

    [Fact]
    public void SetTagData_ValidatesSignatureLength()
    {
        var p = new VipsIccProfile();
        Assert.Throws<ArgumentException>(() => p.SetTagData("abc", new byte[1]));   // 3 chars
        Assert.Throws<ArgumentException>(() => p.SetTagData("abcde", new byte[1])); // 5 chars
    }

    [Fact]
    public void TagsAlignedTo4ByteBoundary()
    {
        var p = new VipsIccProfile
        {
            ProfileClass = VipsIccProfileClass.DisplayDevice,
            DataColorSpace = VipsIccColorSpace.Rgb,
            ConnectionColorSpace = VipsIccColorSpace.Xyz,
        };
        // Use odd-length tag data — serializer should pad next tag to 4-byte alignment.
        p.SetTagData("aaaa", new byte[5]);
        p.SetTagData("bbbb", new byte[5]);
        var bytes = p.ToBytes();
        // Find tag entry for "bbbb" (after sort order)
        uint tagCount = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(128, 4));
        Assert.Equal(2u, tagCount);
        // Each tag offset must be divisible by 4.
        for (uint i = 0; i < tagCount; i++)
        {
            int entryOff = 132 + (int)i * 12;
            uint dataOff = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entryOff + 4, 4));
            Assert.Equal(0u, dataOff % 4);
        }
    }

    // ---- Version helper ----

    [Fact]
    public void VersionString_DecodesBcd()
    {
        var p = new VipsIccProfile { Version = 0x04300000u };
        Assert.Equal("4.3.0", p.VersionString);
        p.Version = 0x02100000u;
        Assert.Equal("2.1.0", p.VersionString);
    }

    // ---- Malformed input ----

    [Fact]
    public void TryParse_NullOrShort_ReturnsNull()
    {
        Assert.Null(VipsIccProfile.TryParse(null));
        Assert.Null(VipsIccProfile.TryParse(Array.Empty<byte>()));
        Assert.Null(VipsIccProfile.TryParse(new byte[127]));   // < 128 bytes
    }

    [Fact]
    public void TryParse_BadMagic_ReturnsNull()
    {
        var bytes = new byte[128];
        // Set magic to "junk" instead of "acsp"
        Encoding.ASCII.GetBytes("junk").CopyTo(bytes.AsSpan(36));
        Assert.Null(VipsIccProfile.TryParse(bytes));
    }

    // ---- Color space / class enum coverage ----

    [Fact]
    public void ProfileClasses_AllRoundTrip()
    {
        foreach (var c in Enum.GetValues<VipsIccProfileClass>())
        {
            if (c == VipsIccProfileClass.Unknown) continue;
            var p = new VipsIccProfile { ProfileClass = c };
            var back = VipsIccProfile.TryParse(p.ToBytes())!;
            Assert.Equal(c, back.ProfileClass);
        }
    }

    [Fact]
    public void ColorSpaces_AllRoundTrip()
    {
        foreach (var cs in Enum.GetValues<VipsIccColorSpace>())
        {
            if (cs == VipsIccColorSpace.Unknown) continue;
            var p = new VipsIccProfile
            {
                ProfileClass = VipsIccProfileClass.DisplayDevice,
                DataColorSpace = cs,
                ConnectionColorSpace = VipsIccColorSpace.Xyz,
            };
            var back = VipsIccProfile.TryParse(p.ToBytes())!;
            Assert.Equal(cs, back.DataColorSpace);
        }
    }

    // ---- VipsImage extension methods ----

    [Fact]
    public void VipsImage_RoundTripIccProfileBlob()
    {
        var profile = MakeMinimalProfile();
        var image = new VipsImage { Width = 10, Height = 10, Bands = 3 };
        image.SetIccProfileTyped(profile);

        var retrieved = image.GetIccProfileTyped();
        Assert.NotNull(retrieved);
        Assert.Equal(VipsIccProfileClass.DisplayDevice, retrieved!.ProfileClass);
        Assert.Equal(VipsIccColorSpace.Rgb, retrieved.DataColorSpace);
        Assert.Equal("description blob", Encoding.ASCII.GetString(retrieved.GetTagData("desc")!));
    }

    [Fact]
    public void VipsImage_NoIccBlob_GetIccProfileTypedReturnsNull()
    {
        var image = new VipsImage { Width = 10, Height = 10 };
        Assert.Null(image.GetIccProfileTyped());
    }
}
