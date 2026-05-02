using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Create;

/// <summary>
/// Synthesise the identity LUT — a <c>(256, 1)</c> single-band image
/// where pixel at column <c>x</c> equals <c>x</c>. The natural starting
/// point for designing custom tone curves: build the identity, mutate
/// the values you want to remap (via arithmetic or
/// <see cref="VipsBuildLut"/>), then apply with <c>Maplut</c>.
/// Mirrors libvips <c>vips_identity</c>.
///
/// <para>With <see cref="Bands"/> &gt; 1 the LUT carries a separate
/// identity per band (still all ramping 0..255). With
/// <see cref="UShort"/> = true the LUT is 65536 wide and Float-typed
/// — the standard 16-bit base for high-precision colour processing.</para>
/// </summary>
public class VipsIdentity : VipsOperation
{
    public VipsImage? Out { get; set; }
    public int Bands { get; set; } = 1;
    public bool UShort { get; set; } = false;
    public int Size { get; set; } = 0; // 0 → 256 / 65536 default

    public override int Build()
    {
        if (Bands < 1) return -1;
        int size = Size > 0 ? Size : (UShort ? 65536 : 256);
        var format = UShort ? VipsBandFormat.UShort : VipsBandFormat.UChar;

        Out = new VipsImage
        {
            Width = size, Height = 1, Bands = Bands, BandFormat = format,
            Interpretation = VipsInterpretation.Histogram,
            XRes = 1, YRes = 1,
            GenerateFn = Generate, ClientB = (size, format, Bands),
        };
        Out.SetPipeline(VipsDemandStyle.Any);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Identity", Bands, UShort, Size);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var (size, format, bands) = ((int, VipsBandFormat, int))b!;
        VipsRect r = outRegion.Valid;
        var outAddr = outRegion.GetAddress(r.Left, 0);

        if (format == VipsBandFormat.UShort)
        {
            for (int x = 0; x < r.Width; x++)
            {
                int gx = r.Left + x;
                ushort v = (ushort)Math.Min(gx, size - 1);
                for (int bnd = 0; bnd < bands; bnd++)
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        outAddr.Slice(x * bands * 2 + bnd * 2, 2), v);
            }
        }
        else
        {
            for (int x = 0; x < r.Width; x++)
            {
                byte v = (byte)Math.Min(r.Left + x, size - 1);
                for (int bnd = 0; bnd < bands; bnd++)
                    outAddr[x * bands + bnd] = v;
            }
        }
        return 0;
    }
}
