using System;
using System.Buffers.Binary;
using System.Text;
using CosmoImage.Operations.Color;
using CosmoImage.Operations.Metadata;
using Xunit;

namespace CosmoImage.Tests;

public class Round88Tests
{
    private static VipsIccProfile MakeBaseProfile(uint version = 0x04300000u)
        => new VipsIccProfile
        {
            Version = version,
            ProfileClass = VipsIccProfileClass.DisplayDevice,
            DataColorSpace = VipsIccColorSpace.Rgb,
            ConnectionColorSpace = VipsIccColorSpace.Xyz,
        };

    // ---- Description / Copyright ----

    [Fact]
    public void Description_V4_RoundTripsViaMluc()
    {
        var p = MakeBaseProfile();
        p.Description = "sRGB IEC61966-2.1";
        var data = p.GetTagData("desc")!;
        // First 4 bytes should be the mluc type signature.
        Assert.Equal("mluc", Encoding.ASCII.GetString(data, 0, 4));
        var back = VipsIccProfile.TryParse(p.ToBytes())!;
        Assert.Equal("sRGB IEC61966-2.1", back.Description);
    }

    [Fact]
    public void Description_V2_RoundTripsViaDesc()
    {
        var p = MakeBaseProfile(version: 0x02100000u);
        p.Description = "Adobe RGB (1998)";
        var data = p.GetTagData("desc")!;
        Assert.Equal("desc", Encoding.ASCII.GetString(data, 0, 4));
        var back = VipsIccProfile.TryParse(p.ToBytes())!;
        Assert.Equal("Adobe RGB (1998)", back.Description);
    }

    [Fact]
    public void Copyright_V4_RoundTripsViaMluc()
    {
        var p = MakeBaseProfile();
        p.Copyright = "Copyright © 2026 ACME Corp";
        var data = p.GetTagData("cprt")!;
        Assert.Equal("mluc", Encoding.ASCII.GetString(data, 0, 4));
        var back = VipsIccProfile.TryParse(p.ToBytes())!;
        Assert.Equal("Copyright © 2026 ACME Corp", back.Copyright);
    }

    [Fact]
    public void Copyright_V2_RoundTripsViaText()
    {
        var p = MakeBaseProfile(version: 0x02100000u);
        p.Copyright = "(c) 2026 ACME";
        var data = p.GetTagData("cprt")!;
        Assert.Equal("text", Encoding.ASCII.GetString(data, 0, 4));
        var back = VipsIccProfile.TryParse(p.ToBytes())!;
        Assert.Equal("(c) 2026 ACME", back.Copyright);
    }

    [Fact]
    public void Description_SetNullRemovesTag()
    {
        var p = MakeBaseProfile();
        p.Description = "X";
        Assert.True(p.ContainsTag("desc"));
        p.Description = null;
        Assert.False(p.ContainsTag("desc"));
        Assert.Null(p.Description);
    }

    [Fact]
    public void TextTagsMissingReturnNull()
    {
        var p = MakeBaseProfile();
        Assert.Null(p.Description);
        Assert.Null(p.Copyright);
    }

    // ---- XYZ tags ----

    [Fact]
    public void WhitePoint_RoundTrip()
    {
        var p = MakeBaseProfile();
        p.WhitePoint = new VipsColorXyz(0.9504, 1.0, 1.0888);  // D65
        var back = VipsIccProfile.TryParse(p.ToBytes())!;
        var wp = back.WhitePoint;
        Assert.NotNull(wp);
        Assert.Equal(0.9504, wp!.Value.X, 1e-4);
        Assert.Equal(1.0, wp.Value.Y, 1e-4);
        Assert.Equal(1.0888, wp.Value.Z, 1e-4);
    }

    [Fact]
    public void BlackPointAndPrimaries_RoundTrip()
    {
        // Approximation of sRGB primaries.
        var p = MakeBaseProfile();
        p.BlackPoint = new VipsColorXyz(0, 0, 0);
        p.RedPrimary = new VipsColorXyz(0.4361, 0.2225, 0.0139);
        p.GreenPrimary = new VipsColorXyz(0.3851, 0.7169, 0.0971);
        p.BluePrimary = new VipsColorXyz(0.1431, 0.0606, 0.7141);
        var back = VipsIccProfile.TryParse(p.ToBytes())!;
        Assert.Equal(0.4361, back.RedPrimary!.Value.X, 1e-4);
        Assert.Equal(0.7169, back.GreenPrimary!.Value.Y, 1e-4);
        Assert.Equal(0.7141, back.BluePrimary!.Value.Z, 1e-4);
        Assert.Equal(0.0, back.BlackPoint!.Value.Y, 1e-6);
    }

    [Fact]
    public void XyzTag_AbsentReturnsNull()
    {
        var p = MakeBaseProfile();
        Assert.Null(p.WhitePoint);
        Assert.Null(p.RedPrimary);
    }

    [Fact]
    public void XyzTag_WrongTypeReturnsNull()
    {
        var p = MakeBaseProfile();
        // Inject a tag of the wrong type at "wtpt".
        p.SetTagData("wtpt", new byte[] { (byte)'t', (byte)'e', (byte)'x', (byte)'t', 0, 0, 0, 0, (byte)'X', 0 });
        Assert.Null(p.WhitePoint);
    }

    // ---- Curves ----

    [Fact]
    public void CurveGamma_SingleValueRoundTrip()
    {
        var p = MakeBaseProfile();
        p.SetTagCurveGamma("rTRC", 2.2);
        Assert.Equal(2.2, p.GetTagCurveGamma("rTRC")!.Value, 1e-2);
        var back = VipsIccProfile.TryParse(p.ToBytes())!;
        Assert.Equal(2.2, back.GetTagCurveGamma("rTRC")!.Value, 1e-2);
    }

    [Fact]
    public void CurveTable_LookupTableRoundTrip()
    {
        // Emulate a small LUT.
        var lut = new ushort[] { 0, 16384, 32768, 49152, 65535 };
        var p = MakeBaseProfile();
        p.SetTagCurveTable("rTRC", lut);
        var back = VipsIccProfile.TryParse(p.ToBytes())!;
        var table = back.GetTagCurveTable("rTRC")!;
        Assert.Equal(lut, table);
        // Gamma form should not match for an LUT-form curve.
        Assert.Null(back.GetTagCurveGamma("rTRC"));
    }

    [Fact]
    public void CurveZeroCount_GammaIsOne()
    {
        // count=0 = identity curve = gamma 1.0.
        var data = new byte[12];
        Encoding.ASCII.GetBytes("curv").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), 0);
        var p = MakeBaseProfile();
        p.SetTagData("rTRC", data);
        Assert.Equal(1.0, p.GetTagCurveGamma("rTRC")!.Value);
    }

    // ---- Mluc preferred-language selection ----

    [Fact]
    public void Mluc_MultiLanguagePicksPreferredFirst()
    {
        // Build a multi-record mluc with both "en" and "de" entries.
        // Layout: 16-byte header + 2 records (24 bytes) + UTF-16BE strings.
        string en = "Hello";
        string de = "Hallo";
        var enBytes = Encoding.BigEndianUnicode.GetBytes(en);
        var deBytes = Encoding.BigEndianUnicode.GetBytes(de);
        int totalSize = 16 + 24 + enBytes.Length + deBytes.Length;
        var data = new byte[totalSize];
        Encoding.ASCII.GetBytes("mluc").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), 2);   // 2 records
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12, 4), 12);
        // Record 0 = de
        Encoding.ASCII.GetBytes("de").CopyTo(data, 16);
        Encoding.ASCII.GetBytes("DE").CopyTo(data, 18);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20, 4), (uint)deBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24, 4), (uint)(16 + 24));
        // Record 1 = en
        Encoding.ASCII.GetBytes("en").CopyTo(data, 28);
        Encoding.ASCII.GetBytes("US").CopyTo(data, 30);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(32, 4), (uint)enBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(36, 4),
            (uint)(16 + 24 + deBytes.Length));
        // Strings
        deBytes.CopyTo(data, 16 + 24);
        enBytes.CopyTo(data, 16 + 24 + deBytes.Length);

        var p = MakeBaseProfile();
        p.SetTagData("desc", data);
        // Default preferred language is "en".
        Assert.Equal("Hello", p.Description);
    }

    // ---- Round-trip preserves typed convenience fields ----

    [Fact]
    public void TypedFields_AllRoundTripTogether()
    {
        var p = MakeBaseProfile();
        p.Description = "Test profile";
        p.Copyright = "Test (c) 2026";
        p.WhitePoint = new VipsColorXyz(0.9504, 1.0, 1.0888);
        p.BlackPoint = new VipsColorXyz(0, 0, 0);
        p.RedPrimary = new VipsColorXyz(0.6, 0.3, 0.05);
        p.GreenPrimary = new VipsColorXyz(0.3, 0.6, 0.1);
        p.BluePrimary = new VipsColorXyz(0.15, 0.05, 0.7);
        p.SetTagCurveGamma("rTRC", 2.2);
        p.SetTagCurveGamma("gTRC", 2.2);
        p.SetTagCurveGamma("bTRC", 2.2);
        var back = VipsIccProfile.TryParse(p.ToBytes())!;
        Assert.Equal("Test profile", back.Description);
        Assert.Equal("Test (c) 2026", back.Copyright);
        Assert.NotNull(back.WhitePoint);
        Assert.NotNull(back.RedPrimary);
        Assert.Equal(2.2, back.GetTagCurveGamma("rTRC")!.Value, 1e-2);
        Assert.Equal(2.2, back.GetTagCurveGamma("gTRC")!.Value, 1e-2);
        Assert.Equal(2.2, back.GetTagCurveGamma("bTRC")!.Value, 1e-2);
    }

    // ---- Wrong-type guards on accessors ----

    [Fact]
    public void GetTagText_WrongTypeReturnsNull()
    {
        var p = MakeBaseProfile();
        // Inject something that isn't text/desc/mluc at "desc".
        p.SetTagData("desc", new byte[] { (byte)'X', (byte)'Y', (byte)'Z', (byte)' ', 0, 0, 0, 0 });
        Assert.Null(p.Description);
    }

    [Fact]
    public void GetTagCurveGamma_WrongTypeReturnsNull()
    {
        var p = MakeBaseProfile();
        p.SetTagData("rTRC", new byte[] { (byte)'X', (byte)'Y', (byte)'Z', (byte)' ', 0, 0, 0, 0 });
        Assert.Null(p.GetTagCurveGamma("rTRC"));
    }
}
