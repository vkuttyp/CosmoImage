using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

/// <summary>
/// FITS (Flexible Image Transport System) loader. Astronomy / scientific
/// imaging format from NASA/IAU; pure-C# implementation, no native deps.
///
/// <para>The format is a sequence of 2880-byte blocks: header blocks contain
/// 80-character ASCII "cards" (KEYWORD = VALUE / comment), terminated by an
/// END card and padded with spaces. Pixel data starts at the next block
/// boundary in big-endian byte order.</para>
///
/// <para>Supported: BITPIX 8 (UChar), 16 / 32 (signed integer → Float with
/// BSCALE/BZERO applied), -32 (IEEE float), -64 (IEEE double → cast to
/// Float). NAXIS = 2 (single-band grayscale); NAXIS = 3 with NAXIS3 in
/// {1, 3, 4} for RGB/RGBA — note that FITS stores multi-band data
/// <i>planar</i> (one full plane after another) so the loader transposes
/// to the interleaved layout VipsImage uses internally.</para>
///
/// <para>Out of scope for first cut: NAXIS ≥ 4 (data cubes), additional
/// HDUs / FITS extensions (binary tables, ASCII tables), WCS coordinate
/// system reconstruction. WCS keywords still round-trip as
/// <c>Metadata["fits:*"]</c> entries.</para>
/// </summary>
public static class VipsFitsLoader
{
    private const int BlockSize = 2880;
    private const int CardSize = 80;
    private const int CardsPerBlock = BlockSize / CardSize; // 36

    public static async ValueTask<bool> IsFitsAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        // First card on a FITS file always starts with "SIMPLE  =". The exact
        // 9-byte prefix is unique enough — random text rarely contains it.
        var sniff = await source.SniffAsync(9, cancellationToken);
        if (sniff.Length < 9) return false;
        var s = sniff.Span;
        return s[0] == 'S' && s[1] == 'I' && s[2] == 'M' && s[3] == 'P' && s[4] == 'L' && s[5] == 'E'
            && s[6] == ' ' && s[7] == ' ' && s[8] == '=';
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        if (!await IsFitsAsync(source, cancellationToken)) return null;

        // Drain into a buffer. FITS files are typically modest (thousands of
        // pixels per axis); streaming variant could come later.
        var ms = new MemoryStream();
        var rawBuffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(rawBuffer, cancellationToken);
            if (read == 0) break;
            ms.Write(rawBuffer, 0, read);
        }
        var bytes = ms.ToArray();

        // ---- Header ----
        var cards = new Dictionary<string, string>(StringComparer.Ordinal);
        int offset = 0;
        bool sawEnd = false;
        while (offset + CardSize <= bytes.Length && !sawEnd)
        {
            // One block at a time. Stop scanning at END but always advance
            // the offset to the next block boundary so the data starts at
            // the right place.
            for (int c = 0; c < CardsPerBlock && offset + CardSize <= bytes.Length; c++)
            {
                var card = System.Text.Encoding.ASCII.GetString(bytes, offset, CardSize);
                offset += CardSize;
                if (card.StartsWith("END") && IsAllSpaces(card, 3)) { sawEnd = true; break; }
                ParseCard(card, cards);
            }
            // Pad to next 2880 boundary.
            int blockBoundary = ((offset + BlockSize - 1) / BlockSize) * BlockSize;
            offset = blockBoundary;
        }
        if (!sawEnd) return null;

        // ---- Required keywords ----
        if (!cards.TryGetValue("SIMPLE", out var simpleVal) || simpleVal.Trim() != "T") return null;
        if (!TryGetInt(cards, "BITPIX", out int bitpix)) return null;
        if (!TryGetInt(cards, "NAXIS", out int naxis)) return null;
        if (naxis < 2 || naxis > 4) return null;

        if (!TryGetInt(cards, "NAXIS1", out int width) || width <= 0) return null;
        if (!TryGetInt(cards, "NAXIS2", out int height) || height <= 0) return null;
        int n3 = 1, n4 = 1;
        if (naxis >= 3)
        {
            if (!TryGetInt(cards, "NAXIS3", out n3) || n3 <= 0) return null;
        }
        if (naxis >= 4)
        {
            if (!TryGetInt(cards, "NAXIS4", out n4) || n4 <= 0) return null;
        }

        // Layout decision (mirrors the NIfTI 4D loader, round 193):
        //   NAXIS = 2, or NAXIS = 3 with NAXIS3 ∈ {1, 3, 4}
        //     → legacy bands-as-channels layout. RGB / RGBA cubes
        //       stay in the natural multi-band shape.
        //   NAXIS = 4, or large NAXIS3 (data cubes)
        //     → 1-band height-stacked layout. n-pages = NAXIS3·NAXIS4,
        //       page-height = NAXIS2. Mirrors multi-page TIFF / GIF.
        bool stackedLayout = naxis >= 4 || (naxis == 3 && n3 != 1 && n3 != 3 && n3 != 4);
        int totalFrames = stackedLayout ? n3 * n4 : 1;
        int planes = stackedLayout ? 1 : n3;

        // BSCALE / BZERO — physical = pixel * BSCALE + BZERO.
        TryGetDouble(cards, "BSCALE", out double bscale, 1.0);
        TryGetDouble(cards, "BZERO", out double bzero, 0.0);
        bool needsScale = bscale != 1.0 || bzero != 0.0;

        // ---- Pixel data ----
        int bytesPerSample = Math.Abs(bitpix) / 8;
        long totalSamples = (long)width * height * n3 * n4;
        long dataLen = totalSamples * bytesPerSample;
        if (offset + dataLen > bytes.Length) return null;

        // Output band-format mirrors libvips' FITS handling: integer types
        // and float widen to Float for downstream consistency, except
        // BITPIX=8 with no scaling stays UChar (the cheap path).
        bool outFloat = bitpix != 8 || needsScale;
        int outSizePel = planes * (outFloat ? 4 : 1);
        int outHeight = stackedLayout ? height * totalFrames : height;
        var pixels = new byte[width * outHeight * outSizePel];

        if (stackedLayout)
        {
            // Stacked: each (NAXIS3, NAXIS4) frame stacked into the
            // output height. Frames in (NAXIS4, NAXIS3) outer-inner
            // order, matching NIfTI's (T, Z) and TIFF multi-page Z/T.
            for (int t = 0; t < n4; t++)
            {
                for (int z = 0; z < n3; z++)
                {
                    int frameIndex = t * n3 + z;
                    long frameSrcBase = offset + (long)frameIndex * width * height * bytesPerSample;
                    int frameDstYBase = frameIndex * height;
                    for (int y = 0; y < height; y++)
                    {
                        long rowOffset = frameSrcBase + (long)y * width * bytesPerSample;
                        for (int x = 0; x < width; x++)
                        {
                            int srcOff = (int)(rowOffset + x * bytesPerSample);
                            double sample = ReadSample(bytes, srcOff, bitpix);
                            if (needsScale) sample = sample * bscale + bzero;

                            int destOff = ((frameDstYBase + y) * width + x) * (outFloat ? 4 : 1);
                            if (outFloat)
                                BinaryPrimitives.WriteSingleLittleEndian(pixels.AsSpan(destOff, 4), (float)sample);
                            else
                                pixels[destOff] = (byte)Math.Clamp(sample, 0, 255);
                        }
                    }
                }
            }
        }
        else
        {
            // Legacy bands-as-channels layout. FITS stores plane 0 first
            // (all WxH samples), then plane 1, etc. VipsImage wants
            // R0,G0,B0,R1,G1,B1,... so we transpose during the decode.
            for (int p = 0; p < planes; p++)
            {
                int planeOffset = offset + p * width * height * bytesPerSample;
                for (int y = 0; y < height; y++)
                {
                    int rowOffset = planeOffset + y * width * bytesPerSample;
                    for (int x = 0; x < width; x++)
                    {
                        int srcOff = rowOffset + x * bytesPerSample;
                        double sample = ReadSample(bytes, srcOff, bitpix);
                        if (needsScale) sample = sample * bscale + bzero;

                        int destOff = (y * width + x) * outSizePel + p * (outFloat ? 4 : 1);
                        if (outFloat)
                            BinaryPrimitives.WriteSingleLittleEndian(pixels.AsSpan(destOff, 4), (float)sample);
                        else
                            pixels[destOff] = (byte)Math.Clamp(sample, 0, 255);
                    }
                }
            }
        }

        var image = new VipsImage
        {
            Width = width,
            Height = outHeight,
            Bands = planes,
            BandFormat = outFloat ? VipsBandFormat.Float : VipsBandFormat.UChar,
            Interpretation = planes == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };

        // Stacked-layout images carry per-axis counts so consumers can
        // split the height stack back into a (NAXIS3, NAXIS4) grid.
        if (stackedLayout)
        {
            image.Metadata["fits:naxis3"] = n3.ToString(System.Globalization.CultureInfo.InvariantCulture);
            image.Metadata["fits:naxis4"] = n4.ToString(System.Globalization.CultureInfo.InvariantCulture);
            image.Metadata["n-pages"] = totalFrames.ToString(System.Globalization.CultureInfo.InvariantCulture);
            image.Metadata["page-height"] = height.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // Surface every card under the fits: namespace so WCS / DATE-OBS /
        // OBSERVER / etc. survive a load → save round-trip even though we
        // don't interpret them.
        foreach (var kv in cards)
        {
            if (kv.Key is "SIMPLE" or "BITPIX" or "NAXIS" or "NAXIS1" or "NAXIS2"
                or "NAXIS3" or "NAXIS4" or "BSCALE" or "BZERO" or "END")
                continue;
            image.Metadata["fits:" + kv.Key] = kv.Value;
        }
        return image;
    }

    private static double ReadSample(byte[] bytes, int offset, int bitpix)
    {
        // FITS is big-endian on the wire. .NET Memory APIs default to little
        // endian, so use the explicit big-endian readers from BinaryPrimitives
        // to avoid manual byte-swapping.
        return bitpix switch
        {
            8 => bytes[offset],
            16 => BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(offset, 2)),
            32 => BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4)),
            -32 => BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(offset, 4)),
            -64 => BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan(offset, 8)),
            _ => 0,
        };
    }

    private static void ParseCard(string card, Dictionary<string, string> cards)
    {
        // Card format: 8-char keyword (left-justified, space-padded), then
        // either "= " for value cards or anything else for comment cards.
        // Value field starts at column 10 (0-indexed); comment after "/".
        // Strings are quoted with single quotes; everything else is a number
        // or boolean (T/F) in fixed-format columns.
        var keyword = card.Substring(0, Math.Min(8, card.Length)).TrimEnd();
        if (keyword.Length == 0) return;
        if (card.Length < 10 || card[8] != '=' || card[9] != ' ') return; // not a value card

        var valueAndComment = card.Substring(10);
        string value;
        if (valueAndComment.TrimStart().StartsWith("'"))
        {
            // Quoted string. Find closing quote (FITS uses doubled '' for
            // literal single quote, but we don't need to round-trip the
            // exact escape — just extract the visible content).
            int start = valueAndComment.IndexOf('\'');
            int end = valueAndComment.IndexOf('\'', start + 1);
            value = end > start ? valueAndComment.Substring(start + 1, end - start - 1).TrimEnd() : "";
        }
        else
        {
            int slash = valueAndComment.IndexOf('/');
            value = (slash >= 0 ? valueAndComment.Substring(0, slash) : valueAndComment).Trim();
        }
        cards[keyword] = value;
    }

    private static bool TryGetInt(Dictionary<string, string> cards, string key, out int value)
    {
        value = 0;
        return cards.TryGetValue(key, out var s)
            && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetDouble(Dictionary<string, string> cards, string key, out double value, double fallback)
    {
        value = fallback;
        if (!cards.TryGetValue(key, out var s)) return false;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsAllSpaces(string s, int from)
    {
        for (int i = from; i < s.Length; i++)
            if (s[i] != ' ') return false;
        return true;
    }
}
