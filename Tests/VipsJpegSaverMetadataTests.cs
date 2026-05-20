using System;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Coverage for VipsJpegSaver's APP1 metadata writing — particularly the
/// multi-segment XMP path (Adobe ExtendedXMP) that closes the
/// previously-thrown stub at WriteApp1MarkerAsync, and the EXIF error case.
/// </summary>
public class VipsJpegSaverMetadataTests
{
    [Fact]
    public async Task LargeXmp_RoundTripsThroughLoader()
    {
        // Encode a JPEG with >64 KB XMP via the saver (StandardXMP stub +
        // ExtendedXMP segments), then read it back via the loader. The
        // loader must reassemble the full XMP and surface it identically
        // through MetadataBlobs["xmp"].
        var xmp = MakeXmpBlob(200_000);
        var jpeg = await EncodeWithMetadata(xmp: xmp);

        var roundtripped = VipsJpegLoader.ExtractXmpWithExtended(jpeg);
        Assert.NotNull(roundtripped);
        Assert.Equal(xmp.Length, roundtripped!.Length);
        Assert.Equal(xmp, roundtripped);
    }

    [Fact]
    public async Task SmallXmp_RoundTripsThroughLoader()
    {
        // Sanity: small XMP (single-segment) still reads back unchanged.
        var xmp = MakeXmpBlob(2048);
        var jpeg = await EncodeWithMetadata(xmp: xmp);
        var roundtripped = VipsJpegLoader.ExtractXmpWithExtended(jpeg);
        Assert.Equal(xmp, roundtripped);
    }

    [Fact]
    public async Task ExtendedXmp_MissingExtensionSegments_FallsBackToStandard()
    {
        // Forge a JPEG that has a StandardXMP stub claiming HasExtendedXMP
        // but no ExtendedXMP segments to back it. The loader should fall
        // back to returning the stub rather than failing.
        const string stub =
            "<?xpacket?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" " +
            "xmlns:xmpNote=\"http://ns.adobe.com/xmp/note/\" " +
            "xmpNote:HasExtendedXMP=\"DEADBEEFDEADBEEFDEADBEEFDEADBEEF\"/>" +
            "</rdf:RDF></x:xmpmeta><?xpacket?>";
        var stubBytes = System.Text.Encoding.UTF8.GetBytes(stub);
        var jpeg = await ForgeJpegWithStandardXmpOnly(stubBytes);

        var result = VipsJpegLoader.ExtractXmpWithExtended(jpeg);
        // Falls back to the stub (the saver / loader contract is "degraded
        // but not silently corrupt").
        Assert.Equal(stubBytes, result);
    }

    /// <summary>Build a minimal valid JPEG carrying only a StandardXMP APP1
    /// payload — no ExtendedXMP segments — for the fallback test.</summary>
    private static async Task<byte[]> ForgeJpegWithStandardXmpOnly(byte[] xmpPayload)
    {
        const int W = 4, H = 4;
        var rgb = new byte[W * H * 3];
        var img = new VipsImage
        {
            Width = W, Height = H, Bands = 3,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0, YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => rgb),
        };
        img.MetadataBlobs["xmp"] = xmpPayload;
        using var ms = new MemoryStream();
        var w = PipeWriter.Create(ms);
        await VipsJpegSaver.SaveAsync(img, w);
        return ms.ToArray();
    }

    [Fact]
    public async Task SmallXmp_WritesSingleStandardApp1()
    {
        var xmp = MakeXmpBlob(1024); // well under 64 KB
        var jpeg = await EncodeWithMetadata(xmp: xmp);

        var (standardOff, _) = FindApp1WithIdentifier(jpeg, VipsJpegLoader.XmpIdentifier);
        Assert.True(standardOff >= 0, "expected one StandardXMP APP1 marker");

        // No ExtendedXMP marker since it fit in a single segment.
        Assert.True(FindExtendedXmpOffset(jpeg) < 0, "expected no ExtendedXMP for small payload");
    }

    [Fact]
    public async Task LargeXmp_WritesStubPlusExtendedSegments()
    {
        // 200 KB XMP forces the multi-segment path (3 ExtendedXMP segments
        // at ~65 KB each, after the stub).
        var xmp = MakeXmpBlob(200_000);
        var jpeg = await EncodeWithMetadata(xmp: xmp);

        // 1. StandardXMP stub present.
        var (standardOff, standardLen) = FindApp1WithIdentifier(jpeg, VipsJpegLoader.XmpIdentifier);
        Assert.True(standardOff >= 0, "missing StandardXMP stub");
        // Stub is small — under 1 KB.
        Assert.True(standardLen < 1024, $"stub unexpectedly large: {standardLen} bytes");

        // 2. Stub contains the HasExtendedXMP GUID = MD5 of the full XMP.
        string expectedGuid = Convert.ToHexString(MD5.HashData(xmp));
        string stubText = System.Text.Encoding.UTF8.GetString(
            jpeg.AsSpan(standardOff + 4 + VipsJpegLoader.XmpIdentifier.Length,
                         standardLen - 2 - VipsJpegLoader.XmpIdentifier.Length));
        Assert.Contains(expectedGuid, stubText);
        Assert.Contains("HasExtendedXMP", stubText);

        // 3. One or more ExtendedXMP segments, all carrying the same GUID,
        //    whose chunks reassemble into the original XMP.
        var reassembled = new byte[xmp.Length];
        int reconBytes = 0;
        int totalSegmentsSeen = 0;
        int scan = 2;
        while (scan < jpeg.Length - 4)
        {
            if (jpeg[scan] == 0xFF && jpeg[scan + 1] == 0xE1)
            {
                int len = (jpeg[scan + 2] << 8) | jpeg[scan + 3];
                int payloadStart = scan + 4;
                if (payloadStart + ExtendedXmpIdentifier.Length <= jpeg.Length &&
                    jpeg.AsSpan(payloadStart, ExtendedXmpIdentifier.Length).SequenceEqual(ExtendedXmpIdentifier))
                {
                    int g = payloadStart + ExtendedXmpIdentifier.Length;
                    string guidHere = System.Text.Encoding.ASCII.GetString(jpeg, g, 32);
                    Assert.Equal(expectedGuid, guidHere);

                    int fullLen =
                        (jpeg[g + 32] << 24) | (jpeg[g + 33] << 16) |
                        (jpeg[g + 34] << 8)  |  jpeg[g + 35];
                    int offset =
                        (jpeg[g + 36] << 24) | (jpeg[g + 37] << 16) |
                        (jpeg[g + 38] << 8)  |  jpeg[g + 39];
                    Assert.Equal(xmp.Length, fullLen);

                    int chunkLen = len - 2 - ExtendedXmpIdentifier.Length - 32 - 4 - 4;
                    jpeg.AsSpan(g + 40, chunkLen).CopyTo(reassembled.AsSpan(offset));
                    reconBytes += chunkLen;
                    totalSegmentsSeen++;
                }
                scan += 2 + len;
            }
            else { scan++; }
        }

        Assert.True(totalSegmentsSeen >= 2, $"expected ≥2 ExtendedXMP segments for 200KB; got {totalSegmentsSeen}");
        Assert.Equal(xmp.Length, reconBytes);
        Assert.Equal(xmp, reassembled);
    }

    [Fact]
    public async Task LargeExif_ThrowsWithHelpfulMessage()
    {
        // EXIF cannot legitimately be split per JEITA CP-3451 — we throw.
        var exif = new byte[80_000];
        for (int i = 0; i < exif.Length; i++) exif[i] = (byte)(i & 0xFF);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => EncodeWithMetadata(exif: exif));

        Assert.Contains("EXIF", ex.Message);
        Assert.Contains("CP-3451", ex.Message);
        Assert.Contains("ExtendedXMP", ex.Message);  // message points users at the XMP alternative
    }

    [Fact]
    public async Task XmpAtExactlySegmentBoundary_StillWorks()
    {
        // Edge: XMP that's exactly at the single-segment cap. Identifier is
        // 29 bytes ("http://ns.adobe.com/xap/1.0/\0"), length field 2, so
        // the max single-segment payload is 65535 - 2 - 29 = 65504 bytes.
        var xmp = MakeXmpBlob(65504);
        var jpeg = await EncodeWithMetadata(xmp: xmp);
        var (off, _) = FindApp1WithIdentifier(jpeg, VipsJpegLoader.XmpIdentifier);
        Assert.True(off >= 0);
        Assert.True(FindExtendedXmpOffset(jpeg) < 0, "boundary case shouldn't trigger ExtendedXMP");
    }

    [Fact]
    public async Task XmpOneByteOver_TriggersExtendedXmp()
    {
        var xmp = MakeXmpBlob(65505); // one over the single-segment cap
        var jpeg = await EncodeWithMetadata(xmp: xmp);
        Assert.True(FindExtendedXmpOffset(jpeg) >= 0, "1 byte over should split");
    }

    // ---- helpers ----

    private static readonly byte[] ExtendedXmpIdentifier =
        System.Text.Encoding.ASCII.GetBytes("http://ns.adobe.com/xmp/extension/\0");

    private static byte[] MakeXmpBlob(int size)
    {
        // Realistic-ish XMP-looking content. Doesn't need to be valid XML
        // for the saver — the saver writes the bytes verbatim.
        var buf = new byte[size];
        var pattern = System.Text.Encoding.ASCII.GetBytes("<x:xmp>filler </x:xmp>");
        for (int i = 0; i < size; i++) buf[i] = pattern[i % pattern.Length];
        return buf;
    }

    private static async Task<byte[]> EncodeWithMetadata(byte[]? exif = null, byte[]? xmp = null)
    {
        const int W = 8, H = 8;
        var rgb = new byte[W * H * 3];
        for (int i = 0; i < rgb.Length; i++) rgb[i] = (byte)((i * 7) & 0xFF);
        var img = new VipsImage
        {
            Width = W, Height = H, Bands = 3,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0, YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => rgb),
        };
        if (exif != null) img.MetadataBlobs["exif"] = exif;
        if (xmp != null)  img.MetadataBlobs["xmp"]  = xmp;

        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await VipsJpegSaver.SaveAsync(img, writer);
        return ms.ToArray();
    }

    /// <summary>Find the first APP1 segment whose payload starts with the given identifier.</summary>
    private static (int offset, int lengthField) FindApp1WithIdentifier(byte[] jpeg, byte[] identifier)
    {
        int i = 2; // skip SOI
        while (i < jpeg.Length - 4)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xE1)
            {
                int len = (jpeg[i + 2] << 8) | jpeg[i + 3];
                if (i + 4 + identifier.Length <= jpeg.Length &&
                    jpeg.AsSpan(i + 4, identifier.Length).SequenceEqual(identifier))
                {
                    return (i, len);
                }
                i += 2 + len;
            }
            else { i++; }
        }
        return (-1, 0);
    }

    private static int FindExtendedXmpOffset(byte[] jpeg)
    {
        var (off, _) = FindApp1WithIdentifier(jpeg, ExtendedXmpIdentifier);
        return off;
    }
}
