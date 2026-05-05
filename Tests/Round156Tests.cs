using System;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 156 — DWA RLE-channel path. Channels named A go to the
/// RLE scheme by the default classifier: zlib over byte-level RLE
/// over per-channel planar samples. Lossless, so HALF samples
/// round-trip bit-exactly.
/// </summary>
public class Round156Tests
{
    /// <summary>
    /// 16×16 single-A HALF DWAA EXR built by Python OpenEXR. Per-pixel
    /// values: <c>(x + y) * 0.05</c> in float16. The A channel name
    /// matches the default RLE-class rule.
    /// </summary>
    private const string LibimfDwaaABase64 =
        "di8xAQIAAABjaGFubmVscwBjaGxpc3QAEwAAAEEAAQAAAAAAAAABAAAAAQAAAABjb21wcmVzc2lvbgBjb21wcmVzc2lvbgABAAAACGRhdGFXaW5kb3cAYm94MmkAEAAAAAAAAAAAAAAADwAAAA8AAABkaXNwbGF5V2luZG93AGJveDJpABAAAAAAAAAAAAAAAA8AAAAPAAAAbGluZU9yZGVyAGxpbmVPcmRlcgABAAAAAHBpeGVsQXNwZWN0UmF0aW8AZmxvYXQABAAAAAAAgD9zY3JlZW5XaW5kb3dDZW50ZXIAdjJmAAgAAAAAAAAAAAAAAHNjcmVlbldpbmRvd1dpZHRoAGZsb2F0AAQAAAAAAIA/dHlwZQBzdHJpbmcADQAAAHNjYW5saW5laW1hZ2UAOgEAAAAAAAAAAAAA3QAAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB/AAAAAAAAAOkBAAAAAAAAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAGAEEACAF42oXKyw3CMBBFUctJNSwQwgH8G8qZPtLG9DTdZIOQ7KwSO2/j3T3SXQ2zslFhZ1idjKhAB5QBHXDlq1mAOqADMlCAeuWmxtzuj+eyvN4f67cQIjA2lhBs/AETMAEzMDeWlGwuPadcO8257z9534EaLLVronrkTGfU7w5Aa6ff";

    [Fact]
    public void Pure_LibimfDwaaA_DecodesExactly()
    {
        var bytes = Convert.FromBase64String(LibimfDwaaABase64);
        var img = PureExrDecoder.TryDecode(bytes);
        Assert.NotNull(img);
        Assert.Equal(16, img!.Width);
        Assert.Equal(16, img.Height);
        Assert.Equal(1, img.Bands);
        Assert.Equal(VipsBandFormat.Float, img.BandFormat);

        var pix = img.PixelsLazy!.Value;
        var got = new float[pix.Length / 4];
        Buffer.BlockCopy(pix, 0, got, 0, pix.Length);

        // RLE is lossless — HALF samples round-trip bit-exactly.
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                float expected = (float)(Half)((x + y) * 0.05);
                Assert.Equal(expected, got[y * 16 + x]);
            }
    }
}
