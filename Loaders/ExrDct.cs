using System;

namespace CosmoImage.Loaders;

/// <summary>
/// 8×8 forward and inverse DCT-II (separable, orthonormal) plus the
/// standard JPEG zigzag scan order. Foundational primitives for
/// OpenEXR DWA's LOSSY_DCT channel path; matches libimf's reference
/// floating-point DCT in <c>internal_dwa.c</c>.
///
/// <para>The scaling factor is the orthonormal one: per 1-D pass,
/// each output is a sum-of-cosines weighted by <c>c(n)</c> where
/// <c>c(0) = 1/√2</c> and <c>c(n) = 1</c> for <c>n &gt; 0</c>, with a
/// <c>×½</c> normalization. Two passes (rows then columns for
/// forward, columns then rows for inverse) make it self-inverse.</para>
/// </summary>
internal static class ExrDct
{
    /// <summary>
    /// Forward 8×8 DCT-II in place. Operates on a row-major 64-element
    /// float array; produces frequency-domain coefficients in the
    /// same layout (DC at [0], horizontal frequencies along row 0,
    /// vertical along column 0).
    /// </summary>
    internal static void Forward8x8InPlace(float[] block)
    {
        Span<float> tmp = stackalloc float[64];
        // Rows: pre-pass that lands frequency-domain row coefficients.
        for (int y = 0; y < 8; y++)
        {
            for (int u = 0; u < 8; u++)
            {
                float sum = 0f;
                for (int x = 0; x < 8; x++)
                    sum += block[y * 8 + x] * (float)Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
                float c = (u == 0) ? 0.7071067811865475f : 1f;
                tmp[y * 8 + u] = c * sum * 0.5f;
            }
        }
        // Columns: complete the 2-D DCT.
        for (int u = 0; u < 8; u++)
        {
            for (int v = 0; v < 8; v++)
            {
                float sum = 0f;
                for (int y = 0; y < 8; y++)
                    sum += tmp[y * 8 + u] * (float)Math.Cos((2 * y + 1) * v * Math.PI / 16.0);
                float c = (v == 0) ? 0.7071067811865475f : 1f;
                block[v * 8 + u] = c * sum * 0.5f;
            }
        }
    }

    /// <summary>
    /// Inverse 8×8 DCT-II in place. Reverses
    /// <see cref="Forward8x8InPlace"/>: takes frequency-domain
    /// coefficients and reconstructs spatial pixels.
    /// </summary>
    internal static void Inverse8x8InPlace(float[] block)
    {
        Span<float> tmp = stackalloc float[64];
        // Inverse columns.
        for (int u = 0; u < 8; u++)
        {
            for (int y = 0; y < 8; y++)
            {
                float sum = 0f;
                for (int v = 0; v < 8; v++)
                {
                    float c = (v == 0) ? 0.7071067811865475f : 1f;
                    sum += c * block[v * 8 + u] * (float)Math.Cos((2 * y + 1) * v * Math.PI / 16.0);
                }
                tmp[y * 8 + u] = sum * 0.5f;
            }
        }
        // Inverse rows.
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                float sum = 0f;
                for (int u = 0; u < 8; u++)
                {
                    float c = (u == 0) ? 0.7071067811865475f : 1f;
                    sum += c * tmp[y * 8 + u] * (float)Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
                }
                block[y * 8 + x] = sum * 0.5f;
            }
        }
    }

    /// <summary>
    /// Standard JPEG zigzag scan order. <c>ZigzagToRowMajor[k]</c> is
    /// the row-major flat index <c>(row * 8 + col)</c> of the k-th
    /// position in the zigzag scan. Used by encoders to serialize
    /// 8×8 frequency blocks into 1-D coefficient streams that
    /// concentrate energy at the front.
    /// </summary>
    internal static readonly int[] ZigzagToRowMajor = new[]
    {
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    };
}
