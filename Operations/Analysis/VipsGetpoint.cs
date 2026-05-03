using System;
using System.Buffers.Binary;

namespace CosmoImage.Operations.Analysis;

/// <summary>
/// Extract a single pixel as a <see cref="double"/>[] of band values.
/// Mirrors libvips <c>vips_getpoint</c>. Coordinates are clipped to
/// the image bounds; out-of-bounds requests throw.
///
/// <para>Static helper rather than a streaming op — there's nothing
/// to compose downstream, callers want the raw values back to drive
/// imperative logic. Handles UChar / UShort / Float / DPComplex
/// inputs with the obvious per-format conversions.</para>
/// </summary>
public static class VipsGetpoint
{
    public static double[] Compute(VipsImage input, int x, int y)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (x < 0 || x >= input.Width || y < 0 || y >= input.Height)
            throw new ArgumentOutOfRangeException($"({x}, {y}) outside {input.Width}x{input.Height}");

        using var reg = new VipsRegion(input);
        reg.Prepare(new VipsRect(x, y, 1, 1));
        var addr = reg.GetAddress(x, y);

        int bands = input.Bands;
        var result = new double[bands];
        switch (input.BandFormat)
        {
            case VipsBandFormat.UChar:
                for (int b = 0; b < bands; b++) result[b] = addr[b];
                break;
            case VipsBandFormat.UShort:
                for (int b = 0; b < bands; b++)
                    result[b] = BinaryPrimitives.ReadUInt16LittleEndian(addr.Slice(b * 2, 2));
                break;
            case VipsBandFormat.Short:
                for (int b = 0; b < bands; b++)
                    result[b] = BinaryPrimitives.ReadInt16LittleEndian(addr.Slice(b * 2, 2));
                break;
            case VipsBandFormat.UInt:
                for (int b = 0; b < bands; b++)
                    result[b] = BinaryPrimitives.ReadUInt32LittleEndian(addr.Slice(b * 4, 4));
                break;
            case VipsBandFormat.Int:
                for (int b = 0; b < bands; b++)
                    result[b] = BinaryPrimitives.ReadInt32LittleEndian(addr.Slice(b * 4, 4));
                break;
            case VipsBandFormat.Float:
                for (int b = 0; b < bands; b++)
                    result[b] = BinaryPrimitives.ReadSingleLittleEndian(addr.Slice(b * 4, 4));
                break;
            case VipsBandFormat.DPComplex:
                // 16 bytes per band: real (8) + imag (8). Result holds
                // them as pairs of doubles.
                var paired = new double[bands * 2];
                for (int b = 0; b < bands; b++)
                {
                    paired[b * 2 + 0] = BinaryPrimitives.ReadDoubleLittleEndian(addr.Slice(b * 16 + 0, 8));
                    paired[b * 2 + 1] = BinaryPrimitives.ReadDoubleLittleEndian(addr.Slice(b * 16 + 8, 8));
                }
                return paired;
            default:
                throw new ArgumentException($"Getpoint not supported for {input.BandFormat}.");
        }
        return result;
    }
}
