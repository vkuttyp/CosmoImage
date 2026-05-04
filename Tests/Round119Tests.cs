using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 119 — TIFF Predictor=3 (floating-point predictor, Adobe
/// Technote 3) for compressed float TIFFs. Pure decoder reverses
/// the byte-shuffle + horizontal-byte-differencing transform per
/// row; tests use a hand-rolled encoder that mirrors the spec
/// exactly so we can validate the round-trip without a libtiff
/// dependency in test code.
/// </summary>
public class Round119Tests
{
    /// <summary>
    /// Apply the spec's encoder transform to a single row of float
    /// bytes. Used by tests to produce predictor-encoded fixtures.
    /// </summary>
    private static void ApplyFpPredictorRow(byte[] row, int samplesPerRow)
    {
        int rowBytes = samplesPerRow * 4;
        // Step 1: byte shuffle (sample-major → byte-major).
        var shuffled = new byte[rowBytes];
        for (int i = 0; i < samplesPerRow; i++)
        {
            shuffled[0 * samplesPerRow + i] = row[i * 4 + 0];
            shuffled[1 * samplesPerRow + i] = row[i * 4 + 1];
            shuffled[2 * samplesPerRow + i] = row[i * 4 + 2];
            shuffled[3 * samplesPerRow + i] = row[i * 4 + 3];
        }
        // Step 2: horizontal byte differencing.
        row[0] = shuffled[0];
        for (int i = 1; i < rowBytes; i++) row[i] = (byte)(shuffled[i] - shuffled[i - 1]);
    }

    /// <summary>
    /// Build a single-strip uncompressed predictor=3 float TIFF.
    /// Pixels in <paramref name="pixels"/> are host-LE; we encode
    /// the predictor byte-by-byte without further byte-swap.
    /// </summary>
    private static byte[] BuildFpPredictorTiff(float[] pixels, int width, int height, int spp)
    {
        var pix = new byte[pixels.Length * 4];
        Buffer.BlockCopy(pixels, 0, pix, 0, pix.Length);

        // Apply FP predictor per row.
        int samplesPerRow = width * spp;
        int rowBytes = samplesPerRow * 4;
        for (int y = 0; y < height; y++)
        {
            var row = new byte[rowBytes];
            Buffer.BlockCopy(pix, y * rowBytes, row, 0, rowBytes);
            ApplyFpPredictorRow(row, samplesPerRow);
            Buffer.BlockCopy(row, 0, pix, y * rowBytes, rowBytes);
        }

        // Same hand-built single-strip TIFF as Round 117, plus Predictor=3.
        const bool le = true;
        int numTags = 11;  // adds Predictor (317)
        int headerSize = 8;
        int ifdSize = 2 + 12 * numTags + 4;
        int ifdOffset = headerSize;
        int pos = ifdOffset + ifdSize;

        int bpsExt = -1;
        if (spp > 2) { bpsExt = pos; pos += spp * 2; }
        int sfExt = -1;
        if (spp > 2) { sfExt = pos; pos += spp * 2; }
        if ((pos & 1) != 0) pos++;
        int stripDataStart = pos;
        pos += pix.Length;

        var buf = new byte[pos];
        buf[0] = 0x49; buf[1] = 0x49;
        WriteU16(buf, 2, 0x002A, le);
        WriteU32(buf, 4, (uint)ifdOffset, le);
        WriteU16(buf, ifdOffset, (ushort)numTags, le);

        int e = ifdOffset + 2;
        void Entry(int tag, ushort type, uint count, byte[] val4)
        {
            WriteU16(buf, e, (ushort)tag, le);
            WriteU16(buf, e + 2, type, le);
            WriteU32(buf, e + 4, count, le);
            Array.Copy(val4, 0, buf, e + 8, 4);
            e += 12;
        }
        void EShort(int tag, ushort v) { var b = new byte[4]; WriteU16(b, 0, v, le); Entry(tag, 3, 1, b); }
        void ELong(int tag, uint v)    { var b = new byte[4]; WriteU32(b, 0, v, le); Entry(tag, 4, 1, b); }

        ELong(256, (uint)width);
        ELong(257, (uint)height);

        // BitsPerSample = 32 per channel.
        if (spp == 1)
        {
            var v = new byte[4]; WriteU16(v, 0, 32, le); Entry(258, 3, 1, v);
        }
        else if (spp == 2)
        {
            var v = new byte[4]; WriteU16(v, 0, 32, le); WriteU16(v, 2, 32, le);
            Entry(258, 3, 2, v);
        }
        else
        {
            var v = new byte[4]; WriteU32(v, 0, (uint)bpsExt, le);
            Entry(258, 3, (uint)spp, v);
            for (int i = 0; i < spp; i++) WriteU16(buf, bpsExt + i * 2, 32, le);
        }

        EShort(259, 1);                       // Compression = None
        EShort(262, 2);                       // Photometric = RGB

        var stripOff = new byte[4]; WriteU32(stripOff, 0, (uint)stripDataStart, le);
        Entry(273, 4, 1, stripOff);

        EShort(277, (ushort)spp);
        ELong(278, (uint)height);

        var stripBC = new byte[4]; WriteU32(stripBC, 0, (uint)pix.Length, le);
        Entry(279, 4, 1, stripBC);

        EShort(317, 3);                       // Predictor = 3 (FP)

        if (spp == 1)
        {
            var v = new byte[4]; WriteU16(v, 0, 3, le); Entry(339, 3, 1, v);
        }
        else if (spp == 2)
        {
            var v = new byte[4]; WriteU16(v, 0, 3, le); WriteU16(v, 2, 3, le);
            Entry(339, 3, 2, v);
        }
        else
        {
            var v = new byte[4]; WriteU32(v, 0, (uint)sfExt, le);
            Entry(339, 3, (uint)spp, v);
            for (int i = 0; i < spp; i++) WriteU16(buf, sfExt + i * 2, 3, le);
        }

        WriteU32(buf, e, 0, le);
        Array.Copy(pix, 0, buf, stripDataStart, pix.Length);
        return buf;
    }

    private static void WriteU16(byte[] buf, int off, ushort v, bool le)
    {
        if (le) { buf[off] = (byte)v; buf[off + 1] = (byte)(v >> 8); }
        else    { buf[off] = (byte)(v >> 8); buf[off + 1] = (byte)v; }
    }
    private static void WriteU32(byte[] buf, int off, uint v, bool le)
    {
        if (le)
        {
            buf[off] = (byte)v; buf[off + 1] = (byte)(v >> 8);
            buf[off + 2] = (byte)(v >> 16); buf[off + 3] = (byte)(v >> 24);
        }
    }

    private static float[] ReadFloats(byte[] raw)
    {
        var result = new float[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, result, 0, raw.Length);
        return result;
    }

    [Fact]
    public void Pure_FpPredictorFloatRgb_RoundTrips()
    {
        int w = 8, h = 4;
        var pixels = new float[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                pixels[o] = x * 0.5f - 1.0f;
                pixels[o + 1] = y * 1.7f;
                pixels[o + 2] = (x + y) * 0.13f + 100.0f;
            }
        var tiff = BuildFpPredictorTiff(pixels, w, h, 3);

        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(VipsBandFormat.Float, img.BandFormat);

        var got = ReadFloats(img.PixelsLazy!.Value);
        for (int i = 0; i < pixels.Length; i++) Assert.Equal(pixels[i], got[i]);
    }

    [Fact]
    public void Pure_FpPredictorFloatGrayscale_RoundTrips()
    {
        int w = 16, h = 8;
        var pixels = new float[w * h];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (float)Math.Sin(i * 0.1) * 50.0f;

        var tiff = BuildFpPredictorTiff(pixels, w, h, 1);
        // Patch photometric to BlackIsZero for grayscale.
        // Tag 262 is at IFD entry index 4 (after 256/257/258/259); its
        // value-or-offset slot lives at entryOffset + 8.
        int photometricEntry = 8 + 2 + 4 * 12;
        // This path actually emits Photometric=2 (RGB) by default — for
        // grayscale we'd fail validation in the decoder. So we fix it up
        // here in the test fixture.
        Assert.Equal(262, tiff[photometricEntry] | (tiff[photometricEntry + 1] << 8));
        WriteU16(tiff, photometricEntry + 8, 1, le: true);

        var img = PureTiffDecoder.TryDecode(tiff);
        Assert.NotNull(img);
        Assert.Equal(VipsInterpretation.BW, img!.Interpretation);
        var got = ReadFloats(img.PixelsLazy!.Value);
        for (int i = 0; i < pixels.Length; i++) Assert.Equal(pixels[i], got[i]);
    }

    [Fact]
    public async Task LoadAsync_FpPredictorFloatTiff_TakesPureFastPath()
    {
        int w = 8, h = 4;
        var pixels = new float[w * h * 3];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = i * 0.25f - 5.0f;
        var tiff = BuildFpPredictorTiff(pixels, w, h, 3);
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(tiff)));
        var img = await VipsTiffLoader.LoadAsync(src);
        Assert.NotNull(img);
        Assert.Equal(VipsBandFormat.Float, img!.BandFormat);
        var got = ReadFloats(img.PixelsLazy!.Value);
        for (int i = 0; i < pixels.Length; i++) Assert.Equal(pixels[i], got[i]);
    }
}
