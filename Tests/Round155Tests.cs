using System;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 155 — EXR DWAA / DWAB infrastructure plus the
/// UNKNOWN-channel path. The 88-byte counter header is parsed,
/// the optional VERSION≥2 channel-rule table is skipped, and the
/// raw-zlib UNKNOWN sub-stream is decoded for non-RGB/Y/A channels.
/// LOSSY_DCT (RGB) and RLE (alpha) paths are follow-up rounds —
/// blocks containing those streams cause Decompress to return
/// false so the caller falls back to Magick.
/// </summary>
public class Round155Tests
{
    /// <summary>
    /// 16×16 single-Z HALF DWAA EXR built by Python OpenEXR (libimf).
    /// Per-pixel values: <c>x*0.1 + y*0.05</c> in float16. The Z
    /// channel name doesn't match RGB/Y/BY/RY/A so the default
    /// classifier puts it in the UNKNOWN scheme.
    /// </summary>
    private const string LibimfDwaaZBase64 =
        "di8xAQIAAABjaGFubmVscwBjaGxpc3QAEwAAAFoAAQAAAAAAAAABAAAAAQAAAABjb21wcmVzc2lvbgBjb21wcmVzc2lvbgABAAAACGRhdGFXaW5kb3cAYm94MmkAEAAAAAAAAAAAAAAADwAAAA8AAABkaXNwbGF5V2luZG93AGJveDJpABAAAAAAAAAAAAAAAA8AAAAPAAAAbGluZU9yZGVyAGxpbmVPcmRlcgABAAAAAHBpeGVsQXNwZWN0UmF0aW8AZmxvYXQABAAAAAAAgD9zY3JlZW5XaW5kb3dDZW50ZXIAdjJmAAgAAAAAAAAAAAAAAHNjcmVlbldpbmRvd1dpZHRoAGZsb2F0AAQAAAAAAIA/dHlwZQBzdHJpbmcADQAAAHNjYW5saW5laW1hZ2UAOgEAAAAAAAAAAAAAxgAAAAIAAAAAAAAAAAIAAAAAAABsAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAHjahctLFcJADAXQ6EACCw6HFJhO8zNQG9FRHfEUT3Xw5u4vUb7y01v+aPSoPScfJCktrKVk+ew3bfXlfw7eafasg6WENLWVbbEN77LFNrzJ8WbHO32xHe92vCnwfgTeHHifgXcG3lfc6VWeAw==";

    private const string LibimfDwabZBase64 =
        "di8xAQIAAABjaGFubmVscwBjaGxpc3QAEwAAAFoAAQAAAAAAAAABAAAAAQAAAABjb21wcmVzc2lvbgBjb21wcmVzc2lvbgABAAAACWRhdGFXaW5kb3cAYm94MmkAEAAAAAAAAAAAAAAADwAAAA8AAABkaXNwbGF5V2luZG93AGJveDJpABAAAAAAAAAAAAAAAA8AAAAPAAAAbGluZU9yZGVyAGxpbmVPcmRlcgABAAAAAHBpeGVsQXNwZWN0UmF0aW8AZmxvYXQABAAAAAAAgD9zY3JlZW5XaW5kb3dDZW50ZXIAdjJmAAgAAAAAAAAAAAAAAHNjcmVlbldpbmRvd1dpZHRoAGZsb2F0AAQAAAAAAIA/dHlwZQBzdHJpbmcADQAAAHNjYW5saW5laW1hZ2UAOgEAAAAAAAAAAAAAxgAAAAIAAAAAAAAAAAIAAAAAAABsAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAHjahctLFcJADAXQ6EACCw6HFJhO8zNQG9FRHfEUT3Xw5u4vUb7y01v+aPSoPScfJCktrKVk+ew3bfXlfw7eafasg6WENLWVbbEN77LFNrzJ8WbHO32xHe92vCnwfgTeHHifgXcG3lfc6VWeAw==";

    /// <summary>
    /// Two-channel UNKNOWN DWAA: Z + W (HALF). Channels stored
    /// alphabetically (W, Z) inside the file's planar layout.
    /// </summary>
    private const string LibimfDwaaZwBase64 =
        "di8xAQIAAABjaGFubmVscwBjaGxpc3QAJQAAAFcAAQAAAAAAAAABAAAAAQAAAFoAAQAAAAAAAAABAAAAAQAAAABjb21wcmVzc2lvbgBjb21wcmVzc2lvbgABAAAACGRhdGFXaW5kb3cAYm94MmkAEAAAAAAAAAAAAAAADwAAAA8AAABkaXNwbGF5V2luZG93AGJveDJpABAAAAAAAAAAAAAAAA8AAAAPAAAAbGluZU9yZGVyAGxpbmVPcmRlcgABAAAAAHBpeGVsQXNwZWN0UmF0aW8AZmxvYXQABAAAAAAAgD9zY3JlZW5XaW5kb3dDZW50ZXIAdjJmAAgAAAAAAAAAAAAAAHNjcmVlbldpbmRvd1dpZHRoAGZsb2F0AAQAAAAAAIA/dHlwZQBzdHJpbmcADQAAAHNjYW5saW5laW1hZ2UATAEAAAAAAAAAAAAAOwIAAAIAAAAAAAAAAAQAAAAAAADhAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAHjahc8dl91AGADgkZXIyqWVK5X09OzZs3M/ksn7Na1EViIrkcpQJbISWYlUIqWRypVKpBJZilRGKpFKpBSphAvx3B/wwKNU/aF+6B/rgz/2p/FcJ1Hq09j0Js/GTEP84B6LQ3VsT93ZJUvSpDtzMTobshgq6CE+5Ed3as5FMiVVqkxr9lmX7cFBBwsg4qk8YxKSMp3T2kSZz3ZQwgVm0FjjgHHSJ3k6ps4spskiKMDDBDFW2KOinHQ6pIWZTJUpyKGFEfbosMMFkRoKhCaYMpszhAYC7LDEC86oqaaBIi54XdQwQIQFepwwpop6UpxzyyOvC4U5tjjinhx1tBByw4EjuZd10WDAHZV0oZk01zzwjcTyJJWsi4gK8jRRzBX3rOSd5PJFWvkp66KlkfbsuOOF9/JJnHyVTn7LIutixyVfeOY7QfksjfyQIP9kZxO7LjxPvJNESnmVi/ySWW6tts+2tuviVrQ8Sy3fZZC/Etl7W9gX6+2bXReFvIiXN5nkxsb2yVb2m+3tH6s+KuXu3WM4uJNKQuJTZ3SmwEEAjR4VuffhQR38UZ9dolNlgvGZBg8KHQbUdEXTtvZ0RdO2VrytNW9rx1c0b+vA21rJtr6Tba1lWz/JtnayrV/lP9XqURs=";

    [Fact]
    public void Pure_LibimfDwaaZ_DecodesExactly()
    {
        AssertSingleZRoundTrips(LibimfDwaaZBase64);
    }

    [Fact]
    public void Pure_LibimfDwabZ_DecodesExactly()
    {
        // DWAB shares the wire format with DWAA — only block height
        // differs (256 vs 32 lines). For our 16×16 image both fit in
        // one block; this test confirms the dispatcher handles both.
        AssertSingleZRoundTrips(LibimfDwabZBase64);
    }

    [Fact]
    public void Pure_LibimfDwaaTwoUnknownChannels_DecodesExactly()
    {
        // Multi-channel UNKNOWN: Z + W. Channels are stored
        // alphabetically (W first, then Z) — output band order
        // matches because our generic-channel resolver passes them
        // through in alphabetical order.
        var bytes = Convert.FromBase64String(LibimfDwaaZwBase64);
        var img = PureExrDecoder.TryDecode(bytes);
        Assert.NotNull(img);
        Assert.Equal(16, img!.Width);
        Assert.Equal(16, img.Height);
        Assert.Equal(2, img.Bands);

        var pix = img.PixelsLazy!.Value;
        var got = new float[pix.Length / 4];
        Buffer.BlockCopy(pix, 0, got, 0, pix.Length);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                float expW = (float)(Half)(x * 0.07 + y * 0.13);
                float expZ = (float)(Half)(x * 0.1 + y * 0.05);
                int o = (y * 16 + x) * 2;
                Assert.Equal(expW, got[o]);
                Assert.Equal(expZ, got[o + 1]);
            }
        }
    }

    private static void AssertSingleZRoundTrips(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var img = PureExrDecoder.TryDecode(bytes);
        Assert.NotNull(img);
        Assert.Equal(16, img!.Width);
        Assert.Equal(16, img.Height);
        Assert.Equal(1, img.Bands);
        Assert.Equal(VipsBandFormat.Float, img.BandFormat);

        var pix = img.PixelsLazy!.Value;
        var got = new float[pix.Length / 4];
        Buffer.BlockCopy(pix, 0, got, 0, pix.Length);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                float expected = (float)(Half)(x * 0.1 + y * 0.05);
                Assert.Equal(expected, got[y * 16 + x]);
            }
    }
}
