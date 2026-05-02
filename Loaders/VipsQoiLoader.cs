using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

/// <summary>
/// QOI (Quite OK Image) loader. Pure-C# implementation against the v1.0
/// <a href="https://qoiformat.org/qoi-specification.pdf">QOI spec</a>;
/// no native dependency.
///
/// <para>Layout: 14-byte header (magic "qoif", uint32 width, uint32 height,
/// 1-byte channels (3 or 4), 1-byte colorspace) followed by a tagged
/// pixel stream and an 8-byte end marker (7 zeros + 0x01). Each pixel is
/// emitted as one of six ops: RGB, RGBA, INDEX (lookup into a 64-entry
/// hash table of recently-seen pixels), DIFF (small per-channel deltas
/// against the previous pixel), LUMA (mid-range delta), or RUN
/// (run-length of identical pixels). The decoder mirrors the encoder's
/// state machine: previous pixel and 64-entry hash table both reset at
/// start, updated on every emitted pixel.</para>
///
/// <para>Output is UChar with bands matching the file's channels field
/// (3 = RGB, 4 = RGBA).</para>
/// </summary>
public static class VipsQoiLoader
{
    private static readonly byte[] EndMarker = { 0, 0, 0, 0, 0, 0, 0, 1 };

    public static async ValueTask<bool> IsQoiAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(4, cancellationToken);
        if (sniff.Length < 4) return false;
        var s = sniff.Span;
        return s[0] == (byte)'q' && s[1] == (byte)'o' && s[2] == (byte)'i' && s[3] == (byte)'f';
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsQoiAsync(source, cancellationToken)) return null;

        var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
        }
        var bytes = ms.ToArray();
        return Decode(bytes);
    }

    /// <summary>Streaming variant — same eager-buffered shape since the decoder needs random access during state-machine reads.</summary>
    public static ValueTask<VipsImage?> LoadStreamingAsync(IVipsSource source, CancellationToken cancellationToken = default)
        => LoadAsync(source, cancellationToken);

    private static VipsImage? Decode(byte[] bytes)
    {
        if (bytes.Length < 14 + 8) return null;
        if (bytes[0] != 'q' || bytes[1] != 'o' || bytes[2] != 'i' || bytes[3] != 'f') return null;

        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4));
        byte channels = bytes[12];
        // colorspace = bytes[13]; informational only per spec.
        if (width == 0 || height == 0) return null;
        if (channels != 3 && channels != 4) return null;

        long pixelCount = (long)width * height;
        var pixels = new byte[pixelCount * channels];

        // Previous pixel starts at (0, 0, 0, 255) per spec.
        byte pr = 0, pg = 0, pb = 0, pa = 255;
        // Hash index table: 64 entries, each (R, G, B, A). Initialised zero.
        var idxR = new byte[64];
        var idxG = new byte[64];
        var idxB = new byte[64];
        var idxA = new byte[64];

        int p = 14;
        int outIdx = 0;
        int run = 0;
        int outBytes = (int)(pixelCount * channels);
        // Position bound for reading new tag bytes — leave the 8-byte end
        // marker untouched. Run-decrement iterations don't advance p, so
        // the bound check belongs in the read branch only, not the loop.
        int tagBound = bytes.Length - 8;

        while (outIdx < outBytes)
        {
            if (run > 0)
            {
                run--;
            }
            else
            {
                if (p >= tagBound) return null;
                byte b1 = bytes[p++];

                if (b1 == 0xFE) // QOI_OP_RGB
                {
                    if (p + 3 > bytes.Length) return null;
                    pr = bytes[p++];
                    pg = bytes[p++];
                    pb = bytes[p++];
                }
                else if (b1 == 0xFF) // QOI_OP_RGBA
                {
                    if (p + 4 > bytes.Length) return null;
                    pr = bytes[p++];
                    pg = bytes[p++];
                    pb = bytes[p++];
                    pa = bytes[p++];
                }
                else
                {
                    int tag = b1 & 0xC0;
                    if (tag == 0x00) // QOI_OP_INDEX
                    {
                        int idx = b1 & 0x3F;
                        pr = idxR[idx]; pg = idxG[idx]; pb = idxB[idx]; pa = idxA[idx];
                    }
                    else if (tag == 0x40) // QOI_OP_DIFF
                    {
                        int dr = ((b1 >> 4) & 0x03) - 2;
                        int dg = ((b1 >> 2) & 0x03) - 2;
                        int db = (b1 & 0x03) - 2;
                        pr = (byte)(pr + dr);
                        pg = (byte)(pg + dg);
                        pb = (byte)(pb + db);
                    }
                    else if (tag == 0x80) // QOI_OP_LUMA
                    {
                        if (p >= bytes.Length) return null;
                        byte b2 = bytes[p++];
                        int dg = (b1 & 0x3F) - 32;
                        int drDg = ((b2 >> 4) & 0x0F) - 8;
                        int dbDg = (b2 & 0x0F) - 8;
                        pr = (byte)(pr + dg + drDg);
                        pg = (byte)(pg + dg);
                        pb = (byte)(pb + dg + dbDg);
                    }
                    else // tag == 0xC0: QOI_OP_RUN
                    {
                        run = b1 & 0x3F;
                        // Spec: run length stored with bias -1 (so 0 = run of 1).
                        // 0x3E and 0x3F are reserved (RGB / RGBA tags).
                    }
                }

                int hi = (pr * 3 + pg * 5 + pb * 7 + pa * 11) & 0x3F;
                idxR[hi] = pr; idxG[hi] = pg; idxB[hi] = pb; idxA[hi] = pa;
            }

            // Emit one pixel.
            pixels[outIdx++] = pr;
            pixels[outIdx++] = pg;
            pixels[outIdx++] = pb;
            if (channels == 4) pixels[outIdx++] = pa;
        }

        if (outIdx != outBytes) return null;

        // Validate end marker (best-effort; missing marker → still return data).
        // Some encoders truncate; don't fail the whole load on it.

        return new VipsImage
        {
            Width = (int)width,
            Height = (int)height,
            Bands = channels,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };
    }
}
