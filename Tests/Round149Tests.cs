using System;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 149 — PXR24 (compression=5) FLOAT and UINT pixel types.
/// PXR24's HALF path was solid since Round 131; FLOAT and UINT were
/// documented as "future round". FLOAT stores the top 24 bits of each
/// float (3 byte-planes per pixel — bottom mantissa byte is dropped,
/// hence the lossy "24-bit float" name); UINT stores all 4 bytes.
/// Both validated bit-exactly against Python OpenEXR-generated fixtures.
/// </summary>
public class Round149Tests
{
    /// <summary>
    /// 4×2 single-Z FLOAT image PXR24-compressed by libimf. Per-pixel
    /// values are <c>x*0.25 + y*0.5</c> — chosen so they all share the
    /// PXR24-friendly property of zero in the bottom mantissa byte
    /// (PXR24 is then lossless).
    /// </summary>
    private const string LibimfPxr24FloatExrBase64 =
        "di8xAQIAAABjaGFubmVscwBjaGxpc3QAEwAAAFkAAgAAAAAAAAABAAAAAQAAAABjb21wcmVzc2lvbgBjb21wcmVzc2lvbgABAAAABWRhdGFXaW5kb3cAYm94MmkAEAAAAAAAAAAAAAAAAwAAAAEAAABkaXNwbGF5V2luZG93AGJveDJpABAAAAAAAAAAAAAAAAMAAAABAAAAbGluZU9yZGVyAGxpbmVPcmRlcgABAAAAAHBpeGVsQXNwZWN0UmF0aW8AZmxvYXQABAAAAAAAgD9zY3JlZW5XaW5kb3dDZW50ZXIAdjJmAAgAAAAAAAAAAAAAAHNjcmVlbldpbmRvd1dpZHRoAGZsb2F0AAQAAAAAAIA/dHlwZQBzdHJpbmcADQAAAHNjYW5saW5laW1hZ2UAOgEAAAAAAAAAAAAAIAAAAAAAAAAAAIA+AAAAPwAAQD8AAAA/AABAPwAAgD8AAKA/";

    /// <summary>
    /// 4×2 single-ID UINT image PXR24-compressed by libimf. Per-pixel
    /// values are <c>(y*4 + x) * 0x10001</c>.
    /// </summary>
    private const string LibimfPxr24UintExrBase64 =
        "di8xAQIAAABjaGFubmVscwBjaGxpc3QAEwAAAFkAAAAAAAAAAAABAAAAAQAAAABjb21wcmVzc2lvbgBjb21wcmVzc2lvbgABAAAABWRhdGFXaW5kb3cAYm94MmkAEAAAAAAAAAAAAAAAAwAAAAEAAABkaXNwbGF5V2luZG93AGJveDJpABAAAAAAAAAAAAAAAAMAAAABAAAAbGluZU9yZGVyAGxpbmVPcmRlcgABAAAAAHBpeGVsQXNwZWN0UmF0aW8AZmxvYXQABAAAAAAAgD9zY3JlZW5XaW5kb3dDZW50ZXIAdjJmAAgAAAAAAAAAAAAAAHNjcmVlbldpbmRvd1dpZHRoAGZsb2F0AAQAAAAAAIA/dHlwZQBzdHJpbmcADQAAAHNjYW5saW5laW1hZ2UAOgEAAAAAAAAAAAAAIAAAAAAAAAABAAEAAgACAAMAAwAEAAQABQAFAAYABgAHAAcA";

    [Fact]
    public void Pure_LibimfPxr24Float_DecodesExactly()
    {
        var bytes = Convert.FromBase64String(LibimfPxr24FloatExrBase64);
        var img = PureExrDecoder.TryDecode(bytes);
        Assert.NotNull(img);
        Assert.Equal(4, img!.Width);
        Assert.Equal(2, img.Height);
        Assert.Equal(1, img.Bands);
        Assert.Equal(VipsBandFormat.Float, img.BandFormat);

        var pix = img.PixelsLazy!.Value;
        var got = new float[pix.Length / 4];
        Buffer.BlockCopy(pix, 0, got, 0, pix.Length);

        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 4; x++)
            {
                float expected = x * 0.25f + y * 0.5f;
                Assert.Equal(expected, got[y * 4 + x]);
            }
    }

    [Fact]
    public void Pure_LibimfPxr24Uint_DecodesExactly()
    {
        var bytes = Convert.FromBase64String(LibimfPxr24UintExrBase64);
        var img = PureExrDecoder.TryDecode(bytes);
        Assert.NotNull(img);
        Assert.Equal(4, img!.Width);
        Assert.Equal(2, img.Height);
        Assert.Equal(1, img.Bands);
        Assert.Equal(VipsBandFormat.UInt, img.BandFormat);

        var pix = img.PixelsLazy!.Value;
        var got = new uint[pix.Length / 4];
        Buffer.BlockCopy(pix, 0, got, 0, pix.Length);

        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 4; x++)
            {
                uint expected = (uint)((y * 4 + x) * 0x10001);
                Assert.Equal(expected, got[y * 4 + x]);
            }
    }
}
