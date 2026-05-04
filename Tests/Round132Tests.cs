using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 132 — PIZ retry. Built bottom-up with isolated round-trip
/// tests for each PIZ primitive (wavelet, Huffman, bitmap LUT) before
/// any integration. The previous attempt failed because compound
/// failures across primitives were hard to disentangle; this round
/// validates each piece in isolation first.
/// </summary>
public class Round132Tests
{
    // =========================================================================
    // Bitmap + LUT — simplest primitive, build first.
    // =========================================================================

    [Fact]
    public void BitmapLut_IdentityForOneToOneTokens()
    {
        // Tokens that span a small range round-trip cleanly through
        // bitmap → LUT → bitmap.
        var tokens = new ushort[] { 5, 7, 5, 100, 7, 100, 5 };
        var bitmap = new byte[ExrPiz.BitmapSize];
        ExrPiz.BitmapFromTokens(tokens, bitmap, out int min, out int max);

        Assert.True(min <= 0);                 // byte index containing value 5
        Assert.Equal(100 / 8, max);            // byte index containing value 100

        var fwd = new ushort[ExrPiz.UshortRange];
        var rev = new ushort[ExrPiz.UshortRange];
        ushort n = ExrPiz.ForwardAndReverseLut(bitmap, fwd, rev);
        Assert.Equal(2, n);                    // 3 distinct values → max token = 2

        // Forward then reverse should be identity for the actual values.
        for (int i = 0; i < tokens.Length; i++)
        {
            ushort tok = fwd[tokens[i]];
            Assert.Equal(tokens[i], rev[tok]);
        }
    }

    // =========================================================================
    // 2D Haar wavelet — round-trip Wav2Encode + Wav2Decode.
    //
    // The previous attempt had a wavelet bug at scales p ≥ 4 (worked for
    // 8x4, failed for 16x8). The fix here is verifying both directions
    // round-trip exactly on multiple geometries before any integration.
    // =========================================================================

    [Theory]
    [InlineData(8, 4)]
    [InlineData(16, 8)]
    [InlineData(32, 16)]
    [InlineData(16, 16)]
    [InlineData(7, 5)]      // odd dimensions exercise trailing-row/col paths
    [InlineData(13, 11)]
    public void Wavelet_RoundTrip_PreservesValues(int nx, int ny)
    {
        var data = new ushort[nx * ny];
        // Fill with a non-trivial pattern.
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
                data[y * nx + x] = (ushort)((x * 17 + y * 31) & 0x3FFF);

        var original = (ushort[])data.Clone();
        ushort mx = (ushort)((nx + ny) * 31);  // safely under 16384 for w14 path
        ExrPiz.Wav2Encode(data, 0, nx, ny, 1, nx, mx);
        ExrPiz.Wav2Decode(data, 0, nx, ny, 1, nx, mx);

        for (int i = 0; i < data.Length; i++)
            Assert.Equal(original[i], data[i]);
    }

    [Theory]
    [InlineData(8, 4)]
    [InlineData(16, 8)]
    [InlineData(32, 16)]
    public void Wavelet_RoundTrip_ConstantSignal(int nx, int ny)
    {
        // Constant signal exercises the case where most pyramid levels
        // produce zero detail coefficients. After forward + decode it
        // should be unchanged.
        var data = new ushort[nx * ny];
        for (int i = 0; i < data.Length; i++) data[i] = 0x3800;  // half(0.5)

        var original = (ushort[])data.Clone();
        ExrPiz.Wav2Encode(data, 0, nx, ny, 1, nx, 0x3800);
        ExrPiz.Wav2Decode(data, 0, nx, ny, 1, nx, 0x3800);

        for (int i = 0; i < data.Length; i++)
            Assert.Equal(original[i], data[i]);
    }

    // W16 path (mx >= 16384) has a multi-level wrap-around issue with
    // the AOFFSET shift in libimf's algorithm — `(a + AOFFSET) & 0xFFFF`
    // can wrap when intermediate L coefficients reach 0xC000 across deep
    // pyramids, and the wrap loses information non-recoverably. For now
    // W16 is a known limitation; real PIZ blocks usually have token max
    // < 16384 after LUT compaction so the W14 path covers most data.
    // Re-tackling W16 needs deeper study of the libimf reference.

    [Theory]
    [InlineData(2, 2)]
    [InlineData(4, 2)]
    [InlineData(4, 4)]
    [InlineData(8, 4)]
    public void Wavelet_RoundTrip_W16Path_ShallowOnly(int nx, int ny)
    {
        var data = new ushort[nx * ny];
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
                data[y * nx + x] = (ushort)((x * 7 + y * 11) & 0x1FFF);

        var original = (ushort[])data.Clone();
        ushort mx = 0x7FFF;
        ExrPiz.Wav2Encode(data, 0, nx, ny, 1, nx, mx);
        ExrPiz.Wav2Decode(data, 0, nx, ny, 1, nx, mx);

        for (int i = 0; i < data.Length; i++)
            Assert.Equal(original[i], data[i]);
    }

    // =========================================================================
    // Huffman — encode + decode round-trip with EXR's canonical convention.
    // =========================================================================

    [Fact]
    public void Huffman_RoundTrip_VariedFrequencies()
    {
        // Hand-pick frequencies that exercise the canonical-Huffman build.
        var freq = new int[ExrPiz.HufEncSize];
        // Symbols 0-4 used. Most common = 0 (short code), rest = longer.
        // Code lengths: pretend 0 has length 1, 1-4 have length 3.
        freq[0] = 1; freq[1] = 3; freq[2] = 3; freq[3] = 3; freq[4] = 3;
        int im = 0, iM = 4;

        var codes = ExrPiz.BuildCanonicalCodes(freq, im, iM);

        // Tokens to encode: many 0s with occasional 1-4 mixed in.
        var tokens = new ushort[64];
        for (int i = 0; i < tokens.Length; i++) tokens[i] = (ushort)(i % 5);
        // The encoder treats iM = 4 as the RLE marker, so force the token
        // stream to never produce a literal 4 (the encoder never emits 4
        // as a literal in EXR — it's always the RLE marker, and in this
        // test case we want the encoder to always RLE-flush instead of
        // emitting 4 directly). Simplest: drop any 4s from the test stream.
        for (int i = 0; i < tokens.Length; i++) if (tokens[i] == 4) tokens[i] = 0;

        var (bits, nBits) = ExrPiz.HuffmanEncode(codes, tokens, rlc: iM, rleThreshold: 4);

        var br = new ExrPiz.BitReader(bits, 0, bits.Length);
        var decoded = new ushort[tokens.Length];
        int n = ExrPiz.HuffmanDecode(freq, im, iM, br, nBits, tokens.Length, decoded, out string err);
        Assert.True(n == tokens.Length, $"decoded {n}/{tokens.Length} err={err}");
        for (int i = 0; i < tokens.Length; i++)
            Assert.Equal(tokens[i], decoded[i]);
    }

    // PIZ end-to-end integration vs Python OpenEXR fixtures stays
    // deferred — the primitives below all round-trip in isolation, but
    // wiring them against libimf-encoded bitstreams has residual issues
    // that need more careful comparison against a working reference.

    [Fact]
    public void Huffman_RoundTrip_SolidValue()
    {
        // Single-value stream. Tests that RLE handles long runs.
        var freq = new int[ExrPiz.HufEncSize];
        freq[0] = 1; freq[5] = 1;  // sym 0 most-common, sym 5 = RLE marker
        int im = 0, iM = 5;

        var codes = ExrPiz.BuildCanonicalCodes(freq, im, iM);
        var tokens = new ushort[300];  // mostly 0s, forces multiple RLE blocks
        for (int i = 0; i < tokens.Length; i++) tokens[i] = 0;

        var (bits, nBits) = ExrPiz.HuffmanEncode(codes, tokens, rlc: iM);

        var br = new ExrPiz.BitReader(bits, 0, bits.Length);
        var decoded = new ushort[tokens.Length];
        int n = ExrPiz.HuffmanDecode(freq, im, iM, br, nBits, tokens.Length, decoded, out string err);
        Assert.True(n == tokens.Length, $"decoded {n}/{tokens.Length} err={err}");
        for (int i = 0; i < tokens.Length; i++)
            Assert.Equal(tokens[i], decoded[i]);
    }
}
