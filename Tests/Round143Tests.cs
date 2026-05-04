using System;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 143 — verify Round 141's PIZ implementation against an
/// actual libimf-encoded bitstream. Round 141 only round-tripped
/// through our own encoder primitives; this test pulls in a real
/// 16×8 RGB-HALF EXR produced by Python's OpenEXR module
/// (libOpenEXR 3.x) so any wire-format divergence shows up.
/// </summary>
public class Round143Tests
{
    /// <summary>
    /// 16×8 PIZ-compressed RGB-HALF EXR. Python encoder script lives
    /// in <c>.scratch/make_piz_with_dump.py</c>; values per channel are
    /// (5x+3y)%64/64 for R, (7x+11y)%64/64 for G, (x^y)/64 for B.
    /// </summary>
    private const string LibimfPizExrBase64 =
        "di8xAQIAAABjaGFubmVscwBjaGxpc3QANwAAAEIAAQAAAAAAAAABAAAAAQAAAEcAAQAAAAAAAAABAAAAAQAAAFIAAQAAAAAAAAABAAAAAQAAAABjb21wcmVzc2lvbgBjb21wcmVzc2lvbgABAAAABGRhdGFXaW5kb3cAYm94MmkAEAAAAAAAAAAAAAAADwAAAAcAAABkaXNwbGF5V2luZG93AGJveDJpABAAAAAAAAAAAAAAAA8AAAAHAAAAbGluZU9yZGVyAGxpbmVPcmRlcgABAAAAAHBpeGVsQXNwZWN0UmF0aW8AZmxvYXQABAAAAAAAgD9zY3JlZW5XaW5kb3dDZW50ZXIAdjJmAAgAAAAAAAAAAAAAAHNjcmVlbldpbmRvd1dpZHRoAGZsb2F0AAQAAAAAAIA/AEEBAAAAAAAAAAAAAAADAAAAAAAkACgAKgAsAC0ALgAvADCAMAAxgDEAMoAyADOAMwAAAC8AM0A1ADdgOEA5IDoAO+A7AC6AMgA1wDZAOCA5AAAALQAxgDMANUA2gDdgOAA5oDlAOuA6gDsAJAAugDEAJAAAACoAKAAtACwALwAugDAAMIAxADGAMgAygDMAM4AxgDRANgA44DjAOaA6gDsAKgAxQDQANsA3wDigOYA6ACoAMIAygDTANQA3IDjAOGA5ADqgOkA74DsALIAwADMAKAAqAAAAJAAuAC8ALAAtADGAMQAwgDAAM4AzADKAMoA1QDeAOGA5QDogOwAAAC8AM0A1ADdgOEA5IDoAO+A7AC6AMQA0QDWANsA3gDggOcA5YDoAO6A7ACgALwAyQDQAKgAoACQAAAAvAC4ALQAsgDEAMYAwADCAMwAzgDIAMiA4ADngOcA6oDsALIAxgDRANgA44DjAOaA6gDsAKgAxgDAAM8A0ADZAN0A44DiAOSA6wDpgOwAAAC0AMYAzADUALAAtAC4ALwAAACQAKAAqADKAMgAzgDMAMIAwADGAMYA5YDpAOwAkADCAM4A1QDeAOGA5QDogOwAAAC8AM0A1ADJANIA1wDYAOKA4QDngOYA6IDvAOwAqADCAMoA0wDUALQAsAC8ALgAkAAAAKgAogDIAMoAzADOAMAAwgDEAMeA6wDsALQAywDSANiA4ADngOcA6oDsALIAxgDRANgA4gDMANUA2gDdgOAA5oDlAOuA6gDsAJAAugDEANEA1gDYALgAvACwALQAoACoAAAAkADOAMwAygDIAMYAxADCAMAAogDAANMA1gDegOIA5YDpAOwAkADCAM4A1QDeAOGA5gDTANQA3IDjAOGA5ADqgOkA74DsALIAwADPANAA2QDcALwAuAC0ALAAqACgAJAAAgDMAM4AyADKAMQAxgDAAMIAyADXANkA4IDkAOuA6wDsALQAywDSANiA4ADngOcA6QDWANsA3gDggOcA5YDoAO6A7ACgALwAyQDSANcA2ADg=";

    [Fact]
    public void Pure_LibimfPizExr_DecodesExactly()
    {
        var bytes = Convert.FromBase64String(LibimfPizExrBase64);
        var img = PureExrDecoder.TryDecode(bytes);
        Assert.NotNull(img);
        Assert.Equal(16, img!.Width);
        Assert.Equal(8, img.Height);
        Assert.Equal(3, img.Bands);
        Assert.Equal(VipsBandFormat.Float, img.BandFormat);

        // Reconstruct the expected per-pixel values, round-tripped
        // through HALF the same way Python's encoder did. The PIZ
        // round-trip is lossless within HALF precision, so we expect
        // bit-exact equality on the float promotions.
        int w = 16, h = 8;
        var pix = img.PixelsLazy!.Value;
        var got = new float[pix.Length / 4];
        Buffer.BlockCopy(pix, 0, got, 0, pix.Length);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float expR = (float)(Half)(((x * 5 + y * 3) % 64) / 64.0);
                float expG = (float)(Half)(((x * 7 + y * 11) % 64) / 64.0);
                float expB = (float)(Half)((x ^ y) / 64.0);
                int o = (y * w + x) * 3;
                Assert.Equal(expR, got[o]);
                Assert.Equal(expG, got[o + 1]);
                Assert.Equal(expB, got[o + 2]);
            }
        }
    }
}
